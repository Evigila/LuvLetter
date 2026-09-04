using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using LuvLetter.Core.Application;
using Microsoft.Win32.SafeHandles;

namespace LuvLetter.Platform.Applications;

/// <summary>Shell discovery and activation use separate message-pumping STA threads.</summary>
internal static class WindowsShell
{
    private static readonly Lazy<ShellWorker> ShellMetadataDiscoveryWorker =
        new(() => new("LuvLetter Shell metadata discovery"));
    private static readonly Lazy<ShellWorker> GeneralDiscoveryWorker =
        new(() => new("LuvLetter application discovery"));
    private static readonly Lazy<ShellWorker> ActivationWorker = new(() => new("LuvLetter application activation"));

    internal static Task<T> DiscoverAsync<T>(Func<T> operation, CancellationToken cancellationToken) =>
        DiscoverAsync("legacy", operation, cancellationToken);

    internal static Task<T> DiscoverAsync<T>(string sourceId, Func<T> operation, CancellationToken cancellationToken)
    {
        var worker = sourceId is "start-menu:user" or "start-menu:common" or "apps-folder"
            ? ShellMetadataDiscoveryWorker
            : GeneralDiscoveryWorker;
        return worker.Value.RunAsync(operation, cancellationToken);
    }

    internal static Task<T> ActivateAsync<T>(Func<T> operation, CancellationToken cancellationToken) =>
        ActivationWorker.Value.RunAsync(operation, cancellationToken);

    internal static ApplicationLaunchResult Execute(
        string target, string? arguments = null, string? workingDirectory = null, bool invokeIdList = false)
    {
        var information = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            // Wait for Shell dispatch and suppress its generic error dialog; report failures in LuvLetter.
            Mask = 0x00000100U | 0x00000400U | (invokeIdList ? 0x0000000CU : 0U),
            File = target,
            Parameters = string.IsNullOrEmpty(arguments) ? null : arguments,
            Directory = string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
            Show = 1,
        };
        var succeeded = ShellExecuteExW(ref information);
        var error = succeeded ? 0 : Marshal.GetLastWin32Error();
        if (information.Process != IntPtr.Zero) CloseHandle(information.Process);
        return succeeded
            ? new(true)
            : Failure(error);
    }

    internal static ApplicationLaunchResult Reveal(string fullPath)
    {
        // An absolute explorer path avoids a PATH/current-directory lookup for the shell itself.
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        return Execute(explorer, $"/select,\"{fullPath}\"");
    }

    internal static ApplicationLaunchResult ExecuteWithSearchPath(string executablePath,
        string searchPath, string? workingDirectory)
    {
        // Shell alias lookup can select a different executable from cwd/PATH before App Paths.
        // A private child environment preserves the registered search path without changing ours.
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executablePath) ?? string.Empty,
        };
        startInfo.Environment.TryGetValue("PATH", out var inheritedPath);
        startInfo.Environment["PATH"] = string.IsNullOrEmpty(inheritedPath)
            ? searchPath : searchPath + ";" + inheritedPath;
        try
        {
            using var process = Process.Start(startInfo);
            // This direct CreateProcess path, unlike Shell activation, always creates a process on success.
            return process is not null ? new(true) : new(false, "Windows 未接受应用启动请求。");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 740)
        {
            // Shell elevation cannot receive this private environment; never retry with altered semantics.
            return new(false, "此应用需要提升权限且依赖专用搜索路径，请使用其原始快捷方式启动。");
        }
    }

    internal static ApplicationLaunchResult Failure(int error) => new(
        false,
        error == 1223 ? "已取消应用启动。" : $"无法打开应用：{new Win32Exception(error).Message}",
        error == 1223);

    internal static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static string? ResolveFilePath(string? path)
    {
        var normalized = NormalizePath(path);
        if (normalized is null) return null;
        try
        {
            using var handle = File.OpenHandle(normalized, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var value = new StringBuilder(32768);
            var length = GetFinalPathNameByHandleW(handle, value, (uint)value.Capacity, 0);
            if (length > 0 && length < value.Capacity)
            {
                var final = value.ToString();
                if (final.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) return "\\\\" + final[8..];
                if (final.StartsWith("\\\\?\\", StringComparison.Ordinal)) return final[4..];
                return final;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A protected packaged executable still has a useful logical path.
        }
        return normalized;
    }

    internal static object CreateCom(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)
        ?? throw new COMException($"Cannot create {progId}.");

    internal static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private sealed class ShellWorker
    {
        private readonly TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ShellWorker(string name)
        {
            var thread = new Thread(() =>
            {
                var result = CoInitializeEx(IntPtr.Zero, 0x2 | 0x4);
                if (result < 0)
                {
                    ready.TrySetException(Marshal.GetExceptionForHR(result)!);
                    return;
                }
                try
                {
                    ready.TrySetResult(Dispatcher.CurrentDispatcher);
                    Dispatcher.Run();
                }
                finally
                {
                    CoUninitialize();
                }
            }) { IsBackground = true, Name = name };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        internal async Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
        {
            var dispatcher = await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            }, DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(false);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        internal int Size;
        internal uint Mask;
        internal IntPtr Window;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? File;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Directory;
        internal int Show;
        internal IntPtr Instance;
        internal IntPtr IdList;
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Class;
        internal IntPtr ClassKey;
        internal uint HotKey;
        internal IntPtr Icon;
        internal IntPtr Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo information);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}

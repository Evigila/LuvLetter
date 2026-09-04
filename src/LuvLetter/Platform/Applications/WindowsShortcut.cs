using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.IO;

namespace LuvLetter.Platform.Applications;

internal sealed record ShortcutApplicationTarget(
    string? TargetPath,
    string Arguments,
    string? WorkingDirectory,
    int ShowCommand,
    bool HasTargetIdList,
    string? LocalizedDisplayName);

internal static class WindowsShortcut
{
    internal static ShortcutApplicationTarget Read(string path)
    {
        var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        try
        {
            ((IPersistFile)instance).Load(path, 0);
            var link = (IShellLinkW)instance;
            var target = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            var workingDirectory = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0x4);
            link.GetArguments(arguments, arguments.Capacity);
            link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);
            link.GetShowCmd(out var showCommand);
            link.GetIDList(out var idList);
            var hasTargetIdList = idList != IntPtr.Zero;
            if (idList != IntPtr.Zero) Marshal.FreeCoTaskMem(idList);
            return new(WindowsShell.ResolveFilePath(target.ToString()), arguments.ToString(),
                WindowsShell.NormalizePath(workingDirectory.ToString()), showCommand,
                hasTargetIdList, ReadLocalizedName(path));
        }
        finally
        {
            WindowsShell.ReleaseCom(instance);
        }
    }

    private static string? ReadLocalizedName(string path)
    {
        object? shell = null;
        object? folder = null;
        object? item = null;
        try
        {
            shell = WindowsShell.CreateCom("Shell.Application");
            folder = ((dynamic)shell).NameSpace(Path.GetDirectoryName(path));
            item = folder is null ? null : ((dynamic)folder).ParseName(Path.GetFileName(path));
            return item is null ? null : ((dynamic)item).Name as string;
        }
        catch (COMException) { return null; }
        finally
        {
            WindowsShell.ReleaseCom(item);
            WindowsShell.ReleaseCom(folder);
            WindowsShell.ReleaseCom(shell);
        }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
    }
}

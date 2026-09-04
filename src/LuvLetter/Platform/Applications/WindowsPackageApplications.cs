using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using LuvLetter.Core.Application;

namespace LuvLetter.Platform.Applications;

internal sealed record PackageApplicationPaths(string? InstallDirectory, string? ExecutablePath, bool Registered);

internal static class WindowsPackageApplications
{
    internal static bool IsPackagedId(string? appId) => !string.IsNullOrWhiteSpace(appId)
        && appId.IndexOf('!') is var separator && separator > 0 && separator < appId.Length - 1
        && appId.IndexOfAny(['\\', '/', '\0']) < 0;

    internal static PackageApplicationPaths Resolve(string appId)
    {
        if (!IsPackagedId(appId)) return new(null, null, false);
        var separator = appId.IndexOf('!');
        var family = appId[..separator];
        var applicationId = appId[(separator + 1)..];
        var names = GetPackageNames(family);
        string? fallbackInstall = null;
        foreach (var name in names)
        {
            var install = GetInstallPath(name);
            if (install is null) continue;
            fallbackInstall ??= install;
            try
            {
                using var reader = XmlReader.Create(Path.Combine(install, "AppxManifest.xml"), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 8 * 1024 * 1024,
                });
                var document = XDocument.Load(reader);
                var application = document.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName == "Application"
                    && string.Equals((string?)element.Attribute("Id"), applicationId, StringComparison.Ordinal));
                if (application is null) continue;
                var executable = (string?)application.Attribute("Executable");
                var executablePath = string.IsNullOrWhiteSpace(executable)
                    ? null : WindowsShell.ResolveFilePath(Path.Combine(install, executable));
                return new(install, executablePath, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or ArgumentException)
            {
                // Shell registration can remain usable when Windows protects package metadata.
            }
        }
        return new(fallbackInstall, null, names.Length != 0);
    }

    internal static ApplicationLaunchResult Activate(string appId)
    {
        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), throwOnError: true)!;
            instance = Activator.CreateInstance(type)!;
            var manager = (IApplicationActivationManager)instance;
            // AO_NOERRORUI: return the HRESULT to our input surface rather than another dialog.
            var result = manager.ActivateApplication(appId, null, 0x2, out _);
            if (result >= 0) return new(true);
            return result == unchecked((int)0x800704C7)
                ? new(false, "已取消应用启动。", true)
                : new(false, $"无法打开应用：{Marshal.GetExceptionForHR(result)?.Message ?? $"0x{result:X8}"}");
        }
        finally
        {
            WindowsShell.ReleaseCom(instance);
        }
    }

    private static string[] GetPackageNames(string family)
    {
        uint count = 0;
        uint length = 0;
        var result = GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
        if (result == 0 && count == 0) return [];
        if (result != 122) throw new Win32Exception(result);
        if (count > 1024 || length > 1024 * 1024) throw new InvalidDataException("Package registration is too large.");
        var names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
        var buffer = Marshal.AllocHGlobal(checked((int)length * sizeof(char)));
        try
        {
            var capacity = count;
            result = GetPackagesByPackageFamily(family, ref count, names, ref length, buffer);
            if (result != 0) throw new Win32Exception(result);
            if (count > capacity) throw new InvalidDataException("Package registration changed during discovery.");
            var values = new string[count];
            for (var index = 0; index < count; index++)
            {
                values[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, index * IntPtr.Size))
                    ?? throw new InvalidDataException("Package name is missing.");
            }
            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(names);
        }
    }

    private static string? GetInstallPath(string name)
    {
        uint length = 0;
        var result = GetPackagePathByFullName(name, ref length, null);
        if (result != 122 || length == 0 || length > 32768) return null;
        var path = new StringBuilder((int)length);
        return GetPackagePathByFullName(name, ref length, path) == 0
            ? WindowsShell.NormalizePath(path.ToString()) : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string familyName, ref uint count,
        IntPtr packageFullNames, ref uint bufferLength, IntPtr buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, StringBuilder? path);

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
        [PreserveSig]
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string? verb, out uint processId);
        [PreserveSig]
        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray, out uint processId);
    }
}

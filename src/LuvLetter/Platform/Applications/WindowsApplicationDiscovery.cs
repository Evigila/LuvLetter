using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using LuvLetter.Core.Application;
using LuvLetter.Platform.Indexing;
using Microsoft.Win32;

namespace LuvLetter.Platform.Applications;

internal sealed record ApplicationDiscoveryResult(string SourceId, ApplicationEntry[] Entries, bool Succeeded, string? Error = null);
internal sealed record ApplicationDiscoverySource(string SourceId, string? PortableRoot = null);

internal sealed class WindowsApplicationDiscovery
{
    private const string AppPathsRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private const int MaximumVisitedEntries = 100_000;
    private const int MaximumApplications = 20_000;
    private const int MaximumDepth = 32;
    internal const string AppsFolderTargetPrefix = "shell:AppsFolder\\";

    internal static string PortableSourceId(string root) =>
        "portable:" + FileIndexMaintenanceOptions.NormalizeScopePath(root).ToLowerInvariant();

    internal static IReadOnlyList<ApplicationDiscoverySource> DescribeSources(IReadOnlyList<string> portableRoots)
    {
        ArgumentNullException.ThrowIfNull(portableRoots);
        var sources = new List<ApplicationDiscoverySource>
        {
            new("start-menu:user"),
            new("start-menu:common"),
            new("app-paths:user"),
            new("app-paths:machine"),
            new("apps-folder"),
            new("system:curated"),
        };
        sources.AddRange(portableRoots.Select(root => new ApplicationDiscoverySource(PortableSourceId(root), root)));
        return sources;
    }

    internal Task<ApplicationDiscoveryResult> DiscoverSourceAsync(
        string sourceId,
        string? portableRoot,
        Func<string, bool> isPathExcluded,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(isPathExcluded);
        Func<ApplicationEntry[]> operation = sourceId switch
        {
            "start-menu:user" when portableRoot is null => () => DiscoverShortcuts(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), sourceId, isPathExcluded, cancellationToken),
            "start-menu:common" when portableRoot is null => () => DiscoverShortcuts(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), sourceId, isPathExcluded, cancellationToken),
            "app-paths:user" when portableRoot is null => () => DiscoverAppPaths(
                RegistryHive.CurrentUser, sourceId, isPathExcluded, cancellationToken),
            "app-paths:machine" when portableRoot is null => () => DiscoverAppPaths(
                RegistryHive.LocalMachine, sourceId, isPathExcluded, cancellationToken),
            "apps-folder" when portableRoot is null => () => DiscoverAppsFolder(isPathExcluded, cancellationToken),
            "system:curated" when portableRoot is null => () => DiscoverCuratedSystemEntries(isPathExcluded),
            _ when portableRoot is not null && sourceId == PortableSourceId(portableRoot) => () =>
                DiscoverPortable(portableRoot, sourceId, isPathExcluded, cancellationToken),
            _ => throw new ArgumentException("The application discovery source is unknown or its portable root does not match.", nameof(sourceId)),
        };
        return DiscoverCoreAsync(sourceId, operation, cancellationToken);
    }

    private static async Task<ApplicationDiscoveryResult> DiscoverCoreAsync(
        string sourceId, Func<ApplicationEntry[]> operation, CancellationToken cancellationToken)
    {
        try
        {
            var entries = await WindowsShell.DiscoverAsync(sourceId, operation, cancellationToken).ConfigureAwait(false);
            return new(sourceId, entries, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { return new(sourceId, [], false, exception.Message); }
    }

    internal Task<IReadOnlyList<ApplicationDiscoveryResult>> DiscoverAsync(
        IReadOnlyList<string> portableRoots,
        Func<string, bool> isPathExcluded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(portableRoots);
        ArgumentNullException.ThrowIfNull(isPathExcluded);
        var tasks = DescribeSources(portableRoots).Select(source => DiscoverSourceAsync(
            source.SourceId, source.PortableRoot, isPathExcluded, cancellationToken));
        return AwaitSourcesAsync(tasks);
    }

    private static async Task<IReadOnlyList<ApplicationDiscoveryResult>> AwaitSourcesAsync(
        IEnumerable<Task<ApplicationDiscoveryResult>> tasks) => await Task.WhenAll(tasks).ConfigureAwait(false);

    private static ApplicationEntry[] DiscoverShortcuts(string root, string source,
        Func<string, bool> isPathExcluded, CancellationToken cancellationToken)
    {
        var entries = new List<ApplicationEntry>();
        foreach (var path in EnumerateFiles(root, ".lnk", isPathExcluded, cancellationToken))
        {
            ShortcutApplicationTarget target;
            try { target = WindowsShortcut.Read(path); }
            catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
            {
                // Broken/non-filesystem shortcuts are not executable registrations.
                continue;
            }
            var targetPath = target.TargetPath;
            var hasFileTarget = !string.IsNullOrEmpty(targetPath);
            var supportedFileTarget = hasFileTarget && File.Exists(targetPath)
                && IsTrustedShortcutTarget(targetPath!);
            if (!(supportedFileTarget || !hasFileTarget && target.HasTargetIdList)
                || IsExcluded(isPathExcluded, targetPath, target.WorkingDirectory)) continue;
            var fileName = CleanDisplayName(Path.GetFileNameWithoutExtension(path));
            var displayName = CleanDisplayName(target.LocalizedDisplayName ?? fileName);
            // Hash the original link bytes so differing elevation/show-state/arguments are never collapsed.
            // Equivalent copied links can merge, while unknown Shell metadata stays conservative.
            using var linkStream = File.OpenRead(path);
            var deduplicationKey = "shortcut:" + Convert.ToHexString(SHA256.HashData(linkStream));
            entries.Add(new ApplicationEntry(StableId(source, path), displayName,
                Aliases(displayName, IsExecutable(targetPath) ? targetPath : null, fileName), ApplicationLaunchKind.Shortcut,
                path, IsExecutable(targetPath) ? targetPath : null, target.WorkingDirectory, target.Arguments, source,
                deduplicationKey, targetPath is null ? null : Path.GetDirectoryName(targetPath)));
        }
        return entries.ToArray();
    }

    private static ApplicationEntry[] DiscoverAppPaths(RegistryHive hive, string source,
        Func<string, bool> isPathExcluded, CancellationToken cancellationToken)
    {
        var registrations = new Dictionary<string, ApplicationEntry>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Native registry view takes precedence within this hive. Cross-hive precedence belongs to the catalog view.
        foreach (var view in Environment.Is64BitOperatingSystem
            ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : [RegistryView.Registry32])
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var appPaths = baseKey.OpenSubKey(AppPathsRegistryPath, writable: false);
            if (appPaths is null) continue;
            foreach (var alias in appPaths.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsExecutable(alias) || alias.IndexOfAny(['\\', '/']) >= 0 || !seenNames.Add(alias)) continue;
                if (seenNames.Count > MaximumApplications) throw new InvalidDataException("App Paths exceeds the application limit.");
                using var key = appPaths.OpenSubKey(alias, writable: false);
                var originalPath = WindowsShell.NormalizePath(key?.GetValue(null) as string);
                var executable = ResolveRegisteredExecutable(originalPath);
                var searchPath = NormalizeSearchPath(key?.GetValue("Path") as string);
                if (!IsExecutable(executable) || !File.Exists(executable)
                    || IsExcluded(isPathExcluded, originalPath, executable, Path.GetDirectoryName(executable))) continue;
                var name = ExecutableDisplayName(executable!);
                var identity = $"{executable!.ToUpperInvariant()}\n{searchPath?.ToUpperInvariant()}";
                registrations.Add(alias, new ApplicationEntry(StableId(source, alias), name,
                    Aliases(name, executable, alias), ApplicationLaunchKind.RegisteredExecutable,
                    alias, executable, Path.GetDirectoryName(executable), null, source,
                    "registered:" + Hash(identity), Path.GetDirectoryName(executable), searchPath));
            }
        }
        return registrations.Values.ToArray();
    }

    internal static bool RegistrationStillMatches(ApplicationEntry entry)
    {
        // Do not use a cached alias after its registration has been redirected or removed.
        var hive = entry.Source switch
        {
            "app-paths:user" => RegistryHive.CurrentUser,
            "app-paths:machine" => RegistryHive.LocalMachine,
            _ => throw new InvalidDataException("The App Paths source is invalid."),
        };
        foreach (var view in Environment.Is64BitOperatingSystem
            ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : [RegistryView.Registry32])
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(AppPathsRegistryPath + "\\" + entry.LaunchTarget, writable: false);
            if (key is null) continue;
            return string.Equals(ResolveRegisteredExecutable(key.GetValue(null) as string), entry.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeSearchPath(key.GetValue("Path") as string), entry.SearchPath,
                    StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static ApplicationEntry[] DiscoverAppsFolder(Func<string, bool> isPathExcluded, CancellationToken cancellationToken)
    {
        object? shell = null;
        object? folder = null;
        object? items = null;
        var entries = new Dictionary<string, ApplicationEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            shell = WindowsShell.CreateCom("Shell.Application");
            folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
            if (folder is null) throw new COMException("Windows AppsFolder is unavailable.");
            items = ((dynamic)folder).Items();
            var count = (int)((dynamic)items).Count;
            if (count > MaximumApplications) throw new InvalidDataException("AppsFolder exceeds the application limit.");
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object? item = null;
                try
                {
                    item = ((dynamic)items).Item(index);
                    if (item is null) continue;
                    var displayName = ((dynamic)item).Name as string;
                    if (string.IsNullOrWhiteSpace(displayName)) continue;
                    displayName = CleanDisplayName(displayName);
                    var appId = ReadProperty(item, "System.AppUserModel.ID");
                    var targetPath = WindowsShell.NormalizePath(ReadProperty(item, "System.Link.TargetParsingPath"));
                    if (IsExcluded(isPathExcluded, targetPath)) continue;
                    if (WindowsPackageApplications.IsPackagedId(appId))
                    {
                        PackageApplicationPaths paths;
                        try { paths = WindowsPackageApplications.Resolve(appId!); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
                        {
                            continue;
                        }
                        if (!paths.Registered || IsExcluded(isPathExcluded, paths.InstallDirectory, paths.ExecutablePath)) continue;
                        entries.TryAdd(appId!, new ApplicationEntry(StableId("apps-folder", appId!), displayName,
                            Aliases(displayName, paths.ExecutablePath), ApplicationLaunchKind.Packaged,
                            appId!, paths.ExecutablePath, null, null, "apps-folder", "package:" + appId!.ToUpperInvariant(),
                            paths.InstallDirectory));
                        continue;
                    }

                    var parsingName = FirstShellIdentity(appId, ReadProperty(item, "System.ParsingName"),
                        ((dynamic)item).Path as string);
                    if (parsingName is null) continue;
                    var shellTarget = AppsFolderTargetPrefix + parsingName;
                    var executable = IsExecutable(targetPath) && File.Exists(targetPath) ? targetPath : null;
                    entries.TryAdd(shellTarget, new ApplicationEntry(StableId("apps-folder", shellTarget), displayName,
                        Aliases(displayName, executable, appId), ApplicationLaunchKind.ShellItem,
                        shellTarget, executable, null, null, "apps-folder", "shell-item:" + shellTarget.ToUpperInvariant(),
                        executable is null ? null : Path.GetDirectoryName(executable)));
                }
                finally
                {
                    WindowsShell.ReleaseCom(item);
                }
            }
        }
        finally
        {
            WindowsShell.ReleaseCom(items);
            WindowsShell.ReleaseCom(folder);
            WindowsShell.ReleaseCom(shell);
        }
        return entries.Values.ToArray();
    }

    private static string? FirstShellIdentity(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var value = candidate.Trim();
            if (value.Length <= 2048 && value.IndexOfAny(['\\', '/', '\0', '\r', '\n']) < 0) return value;
        }
        return null;
    }

    private static ApplicationEntry[] DiscoverPortable(string root, string source,
        Func<string, bool> isPathExcluded, CancellationToken cancellationToken)
    {
        var normalized = WindowsShell.NormalizePath(root);
        if (normalized is null) throw new IOException("The configured portable application root is invalid.");
        if (isPathExcluded(normalized)) return [];
        // A disconnected/removable root is a failed source, not evidence that its applications were removed.
        if ((File.GetAttributes(normalized) & FileAttributes.Directory) == 0)
            throw new IOException("The configured portable application root is not a directory.");
        var entries = new List<ApplicationEntry>();
        foreach (var path in EnumerateFiles(root, ".exe", isPathExcluded, cancellationToken))
        {
            var executable = WindowsShell.ResolveFilePath(path);
            if (executable is null || IsExcluded(isPathExcluded, executable)) continue;
            var directory = Path.GetDirectoryName(executable);
            var name = ExecutableDisplayName(executable);
            entries.Add(new ApplicationEntry(StableId(source, path), name, Aliases(name, executable),
                ApplicationLaunchKind.Executable, path, executable, directory, null, source,
                "executable:" + Hash(executable.ToUpperInvariant()), directory));
        }
        return entries.ToArray();
    }

    private static ApplicationEntry[] DiscoverCuratedSystemEntries(Func<string, bool> isPathExcluded)
    {
        var entries = new List<ApplicationEntry>();
        foreach (var definition in CuratedSystemDefinitions())
        {
            if (definition.LaunchKind == ApplicationLaunchKind.SettingsUri && !SettingsProtocolAvailable()) continue;
            if (!string.IsNullOrEmpty(definition.ExecutablePath) && !File.Exists(definition.ExecutablePath)) continue;
            if (definition.LaunchKind == ApplicationLaunchKind.ShellItem
                && Path.IsPathFullyQualified(definition.LaunchTarget) && !File.Exists(definition.LaunchTarget)) continue;
            if (IsExcluded(isPathExcluded, definition.ExecutablePath,
                Path.IsPathFullyQualified(definition.LaunchTarget) ? definition.LaunchTarget : null)) continue;
            var deduplicationKey = definition.LaunchKind == ApplicationLaunchKind.Executable
                ? "executable:" + Hash(definition.LaunchTarget.ToUpperInvariant())
                : "curated:" + definition.Id;
            entries.Add(new ApplicationEntry("system:curated:" + definition.Id, definition.DisplayName,
                definition.Aliases, definition.LaunchKind, definition.LaunchTarget,
                definition.ExecutablePath, definition.ExecutablePath is null ? null : Path.GetDirectoryName(definition.ExecutablePath),
                null, "system:curated", deduplicationKey,
                definition.ExecutablePath is null ? null : Path.GetDirectoryName(definition.ExecutablePath)));
        }
        return entries.ToArray();
    }

    internal static bool TryGetCuratedSystemDefinition(string id, out CuratedSystemDefinition? definition)
    {
        definition = CuratedSystemDefinitions().FirstOrDefault(candidate => candidate.Id == id);
        return definition is not null;
    }

    internal static bool TryValidateSpecialEntry(ApplicationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Source == "apps-folder" && entry.LaunchKind == ApplicationLaunchKind.ShellItem)
            return entry.Id.StartsWith("apps-folder:", StringComparison.Ordinal)
                && IsTrustedAppsFolderTarget(entry.LaunchTarget);
        const string curatedPrefix = "system:curated:";
        return entry.Source == "system:curated"
            && entry.Id.StartsWith(curatedPrefix, StringComparison.Ordinal)
            && TryGetCuratedSystemDefinition(entry.Id[curatedPrefix.Length..], out var definition)
            && definition is not null
            && definition.LaunchKind == entry.LaunchKind
            && string.Equals(definition.LaunchTarget, entry.LaunchTarget, StringComparison.OrdinalIgnoreCase)
            && string.Equals(definition.ExecutablePath, entry.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsTrustedAppsFolderTarget(string target)
    {
        if (!target.StartsWith(AppsFolderTargetPrefix, StringComparison.Ordinal)) return false;
        var identity = target[AppsFolderTargetPrefix.Length..];
        return identity.Length is > 0 and <= 2048 && identity.IndexOfAny(['\\', '/', '\0', '\r', '\n']) < 0;
    }

    private static CuratedSystemDefinition[] CuratedSystemDefinitions()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string SystemFile(string name) => Path.Combine(system, name);
        return
        [
            new("settings", "Settings", ["Windows Settings", "System Settings", "设置", "系统设置"],
                ApplicationLaunchKind.SettingsUri, "ms-settings:", null),
            Setting("settings-windows-update", "Windows Update", "windowsupdate", "系统更新", "Windows 更新"),
            Setting("settings-display", "Display Settings", "display", "显示设置", "显示"),
            Setting("settings-sound", "Sound Settings", "sound", "声音设置", "声音"),
            Setting("settings-network", "Network Settings", "network", "网络设置", "网络和 Internet"),
            Setting("settings-bluetooth", "Bluetooth Settings", "bluetooth", "蓝牙设置", "蓝牙和设备"),
            Setting("settings-apps-features", "Installed Apps", "appsfeatures", "Apps and Features", "安装的应用", "应用和功能"),
            Setting("settings-default-apps", "Default Apps", "defaultapps", "默认应用"),
            Setting("settings-storage", "Storage Settings", "storagesense", "存储设置", "存储"),
            Setting("settings-privacy", "Privacy Settings", "privacy", "隐私设置", "隐私和安全性"),
            Setting("settings-personalization", "Personalization", "personalization", "个性化"),
            new("control-panel", "Control Panel", ["控制面板"], ApplicationLaunchKind.ShellItem,
                "shell:ControlPanelFolder", SystemFile("control.exe")),
            new("system-properties", "System Properties", ["System", "系统属性"], ApplicationLaunchKind.ControlPanel,
                "Microsoft.System", SystemFile("control.exe")),
            new("programs-features", "Programs and Features", ["Uninstall a program", "程序和功能", "卸载程序"],
                ApplicationLaunchKind.ControlPanel, "Microsoft.ProgramsAndFeatures", SystemFile("control.exe")),
            new("network-sharing", "Network and Sharing Center", ["网络和共享中心"],
                ApplicationLaunchKind.ControlPanel, "Microsoft.NetworkAndSharingCenter", SystemFile("control.exe")),
            new("power-options", "Power Options", ["电源选项"], ApplicationLaunchKind.ControlPanel,
                "Microsoft.PowerOptions", SystemFile("control.exe")),
            Mmc("computer-management", "Computer Management", "compmgmt.msc", "计算机管理"),
            Mmc("device-manager", "Device Manager", "devmgmt.msc", "设备管理器"),
            Mmc("disk-management", "Disk Management", "diskmgmt.msc", "磁盘管理"),
            Mmc("event-viewer", "Event Viewer", "eventvwr.msc", "事件查看器"),
            Mmc("services", "Services", "services.msc", "服务"),
            Mmc("task-scheduler", "Task Scheduler", "taskschd.msc", "任务计划程序"),
            Executable("task-manager", "Task Manager", SystemFile("taskmgr.exe"), "taskmgr", "任务管理器"),
            Executable("command-prompt", "Command Prompt", SystemFile("cmd.exe"), "cmd", "命令提示符"),
            Executable("registry-editor", "Registry Editor", Path.Combine(windows, "regedit.exe"), "regedit", "注册表编辑器"),
            Executable("file-explorer", "File Explorer", Path.Combine(windows, "explorer.exe"), "explorer", "文件资源管理器"),
            Executable("notepad", "Notepad", SystemFile("notepad.exe"), "notepad", "记事本"),
            Executable("windows-powershell", "Windows PowerShell",
                Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"), "powershell"),
        ];

        CuratedSystemDefinition Mmc(string id, string name, string file, params string[] aliases) =>
            new(id, name, aliases.Append(Path.GetFileNameWithoutExtension(file)).ToArray(),
                ApplicationLaunchKind.ShellItem, SystemFile(file), SystemFile("mmc.exe"));
        static CuratedSystemDefinition Executable(string id, string name, string file, params string[] aliases) =>
            new(id, name, aliases.Append(Path.GetFileName(file)).Append(Path.GetFileNameWithoutExtension(file)).ToArray(),
                ApplicationLaunchKind.Executable, file, file);
        static CuratedSystemDefinition Setting(string id, string name, string page, params string[] aliases) =>
            new(id, name, aliases, ApplicationLaunchKind.SettingsUri, "ms-settings:" + page, null);
    }

    private static bool SettingsProtocolAvailable()
    {
        try { using var key = Registry.ClassesRoot.OpenSubKey("ms-settings", writable: false); return key is not null; }
        catch (SecurityException) { return false; }
    }

    private static IEnumerable<string> EnumerateFiles(string root, string extension,
        Func<string, bool> isPathExcluded, CancellationToken cancellationToken)
    {
        var normalized = WindowsShell.NormalizePath(root);
        if (normalized is null || isPathExcluded(normalized)) yield break;
        FileAttributes rootAttributes;
        try { rootAttributes = File.GetAttributes(normalized); }
        catch (DirectoryNotFoundException) { yield break; }
        catch (FileNotFoundException) { yield break; }
        if ((rootAttributes & FileAttributes.Directory) == 0) yield break;
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Application discovery roots must not be reparse points.");
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((normalized, 0));
        var visited = 0;
        var matches = 0;
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(current.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++visited > MaximumVisitedEntries) throw new IOException("Application discovery exceeded 100,000 entries; select narrower portable roots.");
                if (isPathExcluded(path)) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (current.Depth >= MaximumDepth) throw new IOException("Application discovery exceeded its directory depth limit.");
                    pending.Push((path, current.Depth + 1));
                }
                else if (Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
                {
                    if (++matches > MaximumApplications) throw new IOException("Application discovery exceeded 20,000 entries.");
                    yield return path;
                }
            }
        }
    }

    internal static bool IsExcluded(Func<string, bool> predicate, string? first) =>
        !string.IsNullOrWhiteSpace(first) && predicate(first);

    internal static bool IsExcluded(Func<string, bool> predicate, string? first, string? second) =>
        IsExcluded(predicate, first) || IsExcluded(predicate, second);

    internal static bool IsExcluded(Func<string, bool> predicate, string? first, string? second, string? third) =>
        IsExcluded(predicate, first) || IsExcluded(predicate, second) || IsExcluded(predicate, third);

    private static bool IsExecutable(string? path) => !string.IsNullOrWhiteSpace(path)
        && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrustedShortcutTarget(string path) =>
        Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".msc", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".cpl", StringComparison.OrdinalIgnoreCase);

    private static string[] Aliases(string displayName, string? executable, string? registration = null) =>
        new[] { displayName, Path.GetFileName(executable), Path.GetFileNameWithoutExtension(executable),
            registration, Path.GetFileNameWithoutExtension(registration) }
        .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string ExecutableDisplayName(string path)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return CleanDisplayName(description);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or Win32Exception)
        {
        }
        return CleanDisplayName(Path.GetFileNameWithoutExtension(path));
    }

    private static string CleanDisplayName(string value)
    {
        var clean = new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Trim();
        return clean.Length > 512 ? clean[..512] : clean;
    }

    private static string? NormalizeSearchPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var parts = Environment.ExpandEnvironmentVariables(path).Split(';')
            .Select(WindowsShell.NormalizePath).Where(value => value is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return parts.Length == 0 ? null : string.Join(';', parts);
    }

    private static string? ResolveRegisteredExecutable(string? path)
    {
        var normalized = WindowsShell.NormalizePath(path);
        if (normalized is null) return null;
        // App Paths explicitly permits the default target to omit its .exe extension.
        if (!Path.HasExtension(normalized)) normalized += ".exe";
        return WindowsShell.ResolveFilePath(normalized);
    }

    private static string? ReadProperty(object item, string name)
    {
        try { return ((dynamic)item).ExtendedProperty(name) as string; }
        catch (COMException) { return null; }
    }

    private static string StableId(string source, string key) => source + ":" + Hash(key.ToUpperInvariant());
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal sealed record CuratedSystemDefinition(
    string Id,
    string DisplayName,
    string[] Aliases,
    ApplicationLaunchKind LaunchKind,
    string LaunchTarget,
    string? ExecutablePath);

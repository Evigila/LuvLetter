using System.IO;
using System.Security.Cryptography;
using LuvLetter.Core.Application;

namespace LuvLetter.Platform.Applications;

internal sealed class WindowsApplicationLauncher : IApplicationLauncher
{
    private readonly Func<string, bool> isPathExcluded;

    internal WindowsApplicationLauncher(Func<string, bool>? isPathExcluded = null)
    {
        this.isPathExcluded = isPathExcluded ?? (_ => false);
    }

    public Task<ApplicationLaunchResult> OpenAsync(ApplicationEntry entry, CancellationToken cancellationToken) =>
        WindowsShell.ActivateAsync(() => Activate(entry, reveal: false, cancellationToken), cancellationToken);

    public Task<ApplicationLaunchResult> RevealAsync(ApplicationEntry entry, CancellationToken cancellationToken) =>
        WindowsShell.ActivateAsync(() => Activate(entry, reveal: true, cancellationToken), cancellationToken);

    private ApplicationLaunchResult Activate(ApplicationEntry entry, bool reveal, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(entry);
            cancellationToken.ThrowIfCancellationRequested();
            if (WindowsApplicationDiscovery.IsExcluded(isPathExcluded,
                entry.ExecutablePath, entry.WorkingDirectory, entry.InstallDirectory)) return Excluded();

            if (entry.LaunchKind == ApplicationLaunchKind.Packaged)
            {
                if (!WindowsPackageApplications.IsPackagedId(entry.LaunchTarget)) return Unavailable();
                var paths = WindowsPackageApplications.Resolve(entry.LaunchTarget);
                if (!paths.Registered) return Unavailable();
                if (WindowsApplicationDiscovery.IsExcluded(isPathExcluded,
                    paths.InstallDirectory, paths.ExecutablePath)) return Excluded();
                if (reveal)
                {
                    if (File.Exists(paths.ExecutablePath))
                    {
                        return WindowsShell.Reveal(paths.ExecutablePath!);
                    }
                    if (Directory.Exists(paths.InstallDirectory))
                    {
                        return WindowsShell.Execute(paths.InstallDirectory!);
                    }
                    return new(false, "此 Windows 应用没有可定位的文件系统路径。");
                }
                cancellationToken.ThrowIfCancellationRequested();
                return WindowsPackageApplications.Activate(entry.LaunchTarget);
            }

            if (entry.Source == "system:curated")
                return ActivateCurated(entry, reveal, cancellationToken);

            if (entry.LaunchKind == ApplicationLaunchKind.ShellItem)
            {
                if (entry.Source != "apps-folder"
                    || !WindowsApplicationDiscovery.IsTrustedAppsFolderTarget(entry.LaunchTarget)) return Unavailable();
                if (reveal)
                    return File.Exists(entry.ExecutablePath)
                        ? WindowsShell.Reveal(entry.ExecutablePath!)
                        : new(false, "此 Shell 应用没有可定位的普通可执行文件。");
                cancellationToken.ThrowIfCancellationRequested();
                return WindowsShell.Execute(entry.LaunchTarget, invokeIdList: true);
            }

            if (entry.LaunchKind == ApplicationLaunchKind.RegisteredExecutable)
            {
                if (entry.LaunchTarget.IndexOfAny(['\\', '/']) >= 0
                    || !File.Exists(entry.ExecutablePath)
                    || !WindowsApplicationDiscovery.RegistrationStillMatches(entry)) return Changed();
                if (reveal) return WindowsShell.Reveal(entry.ExecutablePath!);
                cancellationToken.ThrowIfCancellationRequested();
                // Never launch the alias: cwd/PATH can shadow a valid App Paths registration.
                return string.IsNullOrEmpty(entry.SearchPath)
                    ? WindowsShell.Execute(entry.ExecutablePath!, workingDirectory: entry.WorkingDirectory)
                    : WindowsShell.ExecuteWithSearchPath(entry.ExecutablePath!, entry.SearchPath, entry.WorkingDirectory);
            }

            var launchPath = WindowsShell.NormalizePath(entry.LaunchTarget);
            if (launchPath is null || !File.Exists(launchPath)) return Unavailable();
            if (isPathExcluded(launchPath)) return Excluded();
            var executable = WindowsShell.ResolveFilePath(launchPath);
            if (entry.LaunchKind == ApplicationLaunchKind.Shortcut)
            {
                if (!Path.GetExtension(launchPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return Unavailable();
                var shortcut = WindowsShortcut.Read(launchPath);
                if (!(shortcut.TargetPath is { } targetPath && File.Exists(targetPath)
                        && IsTrustedShortcutTarget(targetPath)
                    || shortcut.TargetPath is null && shortcut.HasTargetIdList)) return Unavailable();
                if (WindowsApplicationDiscovery.IsExcluded(isPathExcluded,
                    shortcut.TargetPath, shortcut.WorkingDirectory)) return Excluded();
                using var stream = File.OpenRead(launchPath);
                if (entry.DeduplicationKey != "shortcut:" + Convert.ToHexString(SHA256.HashData(stream))) return Changed();
                if (IsExecutable(shortcut.TargetPath)
                    && !string.Equals(shortcut.TargetPath, entry.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return Changed();
                if (reveal)
                    return shortcut.TargetPath is { } revealTarget && File.Exists(revealTarget)
                        ? WindowsShell.Reveal(revealTarget)
                        : WindowsShell.Reveal(launchPath);
                cancellationToken.ThrowIfCancellationRequested();
                return WindowsShell.Execute(launchPath);
            }
            else if (entry.LaunchKind != ApplicationLaunchKind.Executable
                || !Path.GetExtension(launchPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return Unavailable();
            }
            if (WindowsApplicationDiscovery.IsExcluded(isPathExcluded, executable)) return Excluded();
            if (!string.Equals(executable, entry.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return Changed();
            cancellationToken.ThrowIfCancellationRequested();
            if (reveal) return WindowsShell.Reveal(executable ?? launchPath);
            return WindowsShell.Execute(launchPath, workingDirectory: Path.GetDirectoryName(launchPath));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(false, $"无法打开应用：{exception.Message}");
        }
    }

    private ApplicationLaunchResult ActivateCurated(
        ApplicationEntry entry, bool reveal, CancellationToken cancellationToken)
    {
        const string idPrefix = "system:curated:";
        if (!entry.Id.StartsWith(idPrefix, StringComparison.Ordinal)
            || !WindowsApplicationDiscovery.TryGetCuratedSystemDefinition(entry.Id[idPrefix.Length..], out var definition)
            || definition is null
            || definition.LaunchKind != entry.LaunchKind
            || !string.Equals(definition.LaunchTarget, entry.LaunchTarget, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(definition.ExecutablePath, entry.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return Unavailable();
        if (!string.IsNullOrEmpty(definition.ExecutablePath)
            && (!File.Exists(definition.ExecutablePath) || isPathExcluded(definition.ExecutablePath))) return Unavailable();
        if (Path.IsPathFullyQualified(definition.LaunchTarget)
            && (!File.Exists(definition.LaunchTarget) || isPathExcluded(definition.LaunchTarget))) return Unavailable();
        if (reveal)
        {
            var revealTarget = Path.IsPathFullyQualified(definition.LaunchTarget)
                ? definition.LaunchTarget : definition.ExecutablePath;
            return File.Exists(revealTarget) ? WindowsShell.Reveal(revealTarget!)
                : new(false, "此系统入口没有可定位的普通文件。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return definition.LaunchKind switch
        {
            ApplicationLaunchKind.SettingsUri when definition.LaunchTarget.StartsWith("ms-settings:", StringComparison.Ordinal) =>
                WindowsShell.Execute(definition.LaunchTarget),
            ApplicationLaunchKind.ControlPanel when definition.LaunchTarget.StartsWith("Microsoft.", StringComparison.Ordinal) =>
                WindowsShell.Execute(definition.ExecutablePath!, "/name " + definition.LaunchTarget),
            ApplicationLaunchKind.ShellItem when definition.LaunchTarget.StartsWith("shell:", StringComparison.Ordinal) =>
                WindowsShell.Execute(definition.LaunchTarget, invokeIdList: true),
            ApplicationLaunchKind.ShellItem when Path.GetExtension(definition.LaunchTarget)
                .Equals(".msc", StringComparison.OrdinalIgnoreCase) => WindowsShell.Execute(definition.LaunchTarget),
            ApplicationLaunchKind.Executable => WindowsShell.Execute(definition.LaunchTarget,
                workingDirectory: Path.GetDirectoryName(definition.LaunchTarget)),
            _ => Unavailable(),
        };
    }

    private static bool IsTrustedShortcutTarget(string path) =>
        Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".msc", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".cpl", StringComparison.OrdinalIgnoreCase);

    private static bool IsExecutable(string? path) => !string.IsNullOrEmpty(path)
        && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    private static ApplicationLaunchResult Unavailable() => new(false, "应用已卸载、移动或当前不可访问，请刷新索引后重试。");
    private static ApplicationLaunchResult Changed() => new(false, "应用启动项已发生变化，请刷新索引后重试。");
    private static ApplicationLaunchResult Excluded() => new(false, "此应用位于完全忽略范围内。");
}

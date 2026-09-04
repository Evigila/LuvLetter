namespace LuvLetter.Core.Application;

public enum ApplicationLaunchKind
{
    Shortcut,
    Executable,
    Packaged,
    RegisteredExecutable,
    ShellItem,
    SettingsUri,
    ControlPanel,
}

public sealed record ApplicationEntry(
    string Id,
    string DisplayName,
    string[] Aliases,
    ApplicationLaunchKind LaunchKind,
    string LaunchTarget,
    string? ExecutablePath = null,
    string? WorkingDirectory = null,
    string? Arguments = null,
    string Source = "",
    string? DeduplicationKey = null,
    string? InstallDirectory = null,
    string? SearchPath = null);

public sealed record ApplicationMatch(ApplicationEntry Entry, int MatchScore);

public interface IApplicationCatalog
{
    event Action? Changed;
    IReadOnlyList<ApplicationMatch> Query(string query, int maximumResults);
    bool TryGet(string id, out ApplicationEntry? entry);
    void RequestRefresh();
}

public sealed record ApplicationLaunchResult(bool Succeeded, string? Message = null, bool Cancelled = false);

public interface IApplicationLauncher
{
    Task<ApplicationLaunchResult> OpenAsync(ApplicationEntry entry, CancellationToken cancellationToken);
    Task<ApplicationLaunchResult> RevealAsync(ApplicationEntry entry, CancellationToken cancellationToken);
}

using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LuvLetter.Platform.Indexing;

internal sealed class FileIndexClientOptions
{
    internal string IndexerExecutablePath { get; init; } = Path.Combine(
        AppContext.BaseDirectory,
        "LuvLetter.Indexer.exe");

    internal string DataDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LuvLetter",
        "Index",
        "v1");

    internal IReadOnlyList<string> Roots { get; init; } = CreateDefaultRoots();

    internal FileIndexMaintenanceOptions Maintenance { get; init; } = FileIndexMaintenanceOptions.LoadDefault();

    internal TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(2);

    internal TimeSpan QueryTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    internal string? DiagnosticLogPath()
    {
        var value = Environment.GetEnvironmentVariable("LUVLETTER_INDEXER_LOG")?.Trim();
        if (string.IsNullOrEmpty(value)
            || string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)
            || value == "0")
        {
            return null;
        }
        if (value == "1" || string.Equals(value, "debug", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(DataDirectory, "logs", "indexer.log");
        }
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }

    internal IReadOnlyList<FileIndexPartitionDescriptor> NormalizedPartitions() => NormalizedPartitions(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        GetDownloadsPath());

    internal IReadOnlyList<FileIndexPartitionDescriptor> NormalizedPartitions(
        string? userProfile, string? desktopPath, string? downloadsPath)
    {
        var roots = Roots.Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath).Select(Path.TrimEndingDirectorySeparator)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static root => root.Length)
            .ThenBy(static root => root, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var configuredRoots = roots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var profile = NormalizeKnownFolder(userProfile);
        var desktop = NormalizeKnownFolder(desktopPath);
        var downloads = NormalizeKnownFolder(downloadsPath);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var descriptors = new List<FileIndexPartitionDescriptor>();

        Add("filesystem:desktop", desktop, FileIndexMaintenanceTier.StartupCritical);
        Add("filesystem:downloads", downloads, FileIndexMaintenanceTier.StartupCritical);
        if (profile is not null && roots.Contains(profile, StringComparer.OrdinalIgnoreCase))
        {
            var delegated = descriptors.Select(item => item.Root)
                .Where(root => IsSameOrChild(profile, root) && !root.Equals(profile, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            descriptors.Add(Create("filesystem:user-profile", profile, delegated, FileIndexMaintenanceTier.Normal));
            claimed.Add(profile);
        }
        foreach (var root in roots)
        {
            if (claimed.Contains(root)) continue;
            Add("filesystem:root-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..16].ToLowerInvariant(),
                root, FileIndexMaintenanceTier.Normal);
        }
        for (var index = 0; index < descriptors.Count; ++index)
        {
            var parent = descriptors[index];
            var delegated = descriptors
                .Where(child => !child.Root.Equals(parent.Root, StringComparison.OrdinalIgnoreCase)
                    && IsSameOrChild(parent.Root, child.Root))
                .Select(static child => child.Root)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static path => path.Length)
                .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            descriptors[index] = parent with { DelegatedSubtrees = delegated };
        }
        return descriptors;

        void Add(string id, string? root, FileIndexMaintenanceTier tier)
        {
            if (root is null || !configuredRoots.Contains(root) || !claimed.Add(root)) return;
            descriptors.Add(Create(id, root, [], tier));
        }

        FileIndexPartitionDescriptor Create(string id, string root, string[] delegated, FileIndexMaintenanceTier tier) =>
            new(id, root, delegated, tier,
                tier == FileIndexMaintenanceTier.StartupCritical
                    ? Maintenance.RefreshIntervalSeconds : Maintenance.NormalPartitionRefreshIntervalSeconds,
                Maintenance.AutomaticRebuildGapSeconds);
    }

    private static string? NormalizeKnownFolder(string? path) => string.IsNullOrWhiteSpace(path)
        ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(IndexerExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(DataDirectory);
        ArgumentNullException.ThrowIfNull(Roots);
        ArgumentNullException.ThrowIfNull(Maintenance);
        Maintenance.Validate();
        if (ConnectionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectionTimeout));
        }

        if (RestartDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RestartDelay));
        }

        if (QueryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(QueryTimeout));
        }
    }

    private static IReadOnlyList<string> CreateDefaultRoots()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return CreateDefaultRoots(userProfile,
        [
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        ], GetDownloadsPath());
    }

    internal static IReadOnlyList<string> CreateDefaultRoots(
        string userProfile, IEnumerable<string> knownFolders, string? downloads)
    {
        var candidates = new List<string> { userProfile };
        candidates.AddRange(knownFolders);
        if (!string.IsNullOrWhiteSpace(downloads)) candidates.Add(downloads);
        return candidates
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static string? GetDownloadsPath()
    {
        // Downloads has no Environment.SpecialFolder value. Ask Windows for its
        // current location, including user redirection, without creating a folder
        // or guessing a fallback beneath the profile when resolution fails.
        var folderId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        var path = IntPtr.Zero;
        try
        {
            return SHGetKnownFolderPath(in folderId, 0, IntPtr.Zero, out path) >= 0
                ? Marshal.PtrToStringUni(path)
                : null;
        }
        finally
        {
            if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path);
        }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        in Guid folderId, uint flags, IntPtr token, out IntPtr path);

    private static bool IsSameOrChild(string parent, string candidate)
    {
        if (string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parentPrefix = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : string.Concat(parent, Path.DirectorySeparatorChar);
        return candidate.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase);
    }
}

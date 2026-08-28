using System.IO;

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

    internal TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal TimeSpan RestartDelay { get; init; } = TimeSpan.FromSeconds(2);

    internal TimeSpan QueryTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    internal IReadOnlyList<string> NormalizedRoots()
    {
        var normalized = Roots
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Select(static root => Path.TrimEndingDirectorySeparator(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static root => root.Length)
            .ThenBy(static root => root, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var retained = new List<string>(normalized.Length);
        foreach (var candidate in normalized)
        {
            if (IsReparseDirectory(candidate)
                || !retained.Any(parent => IsSameOrChild(parent, candidate)))
            {
                retained.Add(candidate);
            }
        }

        return retained;
    }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(IndexerExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(DataDirectory);
        ArgumentNullException.ThrowIfNull(Roots);
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
        var candidates = new[]
        {
            userProfile,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            string.IsNullOrWhiteSpace(userProfile)
                ? string.Empty
                : Path.Combine(userProfile, "Downloads"),
        };
        return candidates
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsReparseDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0
                && (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

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

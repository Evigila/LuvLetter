using System.IO;
using System.Text.Json;

namespace LuvLetter.Platform.Indexing;

internal sealed class FileIndexMaintenanceOptions
{
    public int RefreshIntervalSeconds { get; init; } = 360;

    public int TriggerCooldownSeconds { get; init; } = 60;

    public string[] FullIgnorePaths { get; init; } = [];

    internal bool IsAvailable { get; init; } = true;

    public string[] IgnoreRebuildDirectories { get; init; } =
    [
        Path.GetTempPath(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LuvLetter", "Index"),
    ];

    // Exact directory components, not globs or repository-root exclusions.
    // Initializers also supply defaults when older JSON omits these new fields.
    public string[] IgnoreRebuildDirectoryNames { get; init; } = FileIndexIgnoreDefaults.CreateDirectoryNames();

    public string[] IgnoreRebuildCacheDirectories { get; init; } = FileIndexIgnoreDefaults.CreateCacheDirectories();

    internal string[] NormalizedIgnoreDirectories() => IgnoreRebuildDirectories
        .Concat(IgnoreRebuildCacheDirectories)
        .Select(Environment.ExpandEnvironmentVariables)
        .Select(Path.GetFullPath)
        .Select(Path.TrimEndingDirectorySeparator)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    internal string[] NormalizedIgnoreDirectoryNames() => IgnoreRebuildDirectoryNames
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    internal string[] NormalizedFullIgnorePaths() => FullIgnorePaths
        .Select(Environment.ExpandEnvironmentVariables)
        .Select(NormalizeScopePath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    internal static string NormalizeScopePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(RemoveExtendedPathPrefix(path)));

    private static string RemoveExtendedPathPrefix(string path)
    {
        path = path.Replace('/', '\\');
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(@"\\", path.AsSpan(8));
        }
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)
            && path.Length >= 7 && path[5] == ':' && path[6] == '\\')
        {
            return path[4..];
        }
        return path;
    }

    private static bool IsValidFullIgnorePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        path = RemoveExtendedPathPrefix(Environment.ExpandEnvironmentVariables(path));
        var isDrive = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
        var isUnc = path.StartsWith(@"\\", StringComparison.Ordinal);
        if ((!isDrive && !isUnc) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }
        var components = path[(isDrive ? 3 : 2)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (isUnc && components.Length < 2)
        {
            return false;
        }
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            if (component is "." or "..")
            {
                if (isUnc && index < 2) return false;
                continue;
            }
            if (component.Length > 255 || component.EndsWith('.') || component.EndsWith(' ')
                || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }
        }
        return true;
    }

    internal void Validate()
    {
        if (RefreshIntervalSeconds is < 60 or > 86400
            || TriggerCooldownSeconds is < 1 or > 3600)
        {
            throw new InvalidDataException("Index refresh must be 60-86400 seconds and trigger cooldown 1-3600 seconds.");
        }

        if (IgnoreRebuildDirectories is null || IgnoreRebuildCacheDirectories is null
            || IgnoreRebuildDirectories.Length > 1024 - IgnoreRebuildCacheDirectories.Length
            || IgnoreRebuildDirectories.Concat(IgnoreRebuildCacheDirectories).Any(path => string.IsNullOrWhiteSpace(path)
                || !Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(path))))
        {
            throw new InvalidDataException("Index ignore entries must be absolute directory paths (at most 1024).");
        }

        if (IgnoreRebuildDirectoryNames is null || IgnoreRebuildDirectoryNames.Length > 128
            || IgnoreRebuildDirectoryNames.Any(name => string.IsNullOrWhiteSpace(name)
                || name.Length > 255 || name is "." or ".."
                || name.EndsWith('.') || name.EndsWith(' ')
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException("Index ignore names must be single directory names without wildcards (at most 128).");
        }

        _ = NormalizedIgnoreDirectories();
        if (FullIgnorePaths is null || FullIgnorePaths.Length > 1024
            || FullIgnorePaths.Any(path => !IsValidFullIgnorePath(path)))
        {
            throw new InvalidDataException("Full ignore entries must be exact absolute drive or UNC paths without wildcards or invalid components (at most 1024).");
        }
        _ = NormalizedFullIgnorePaths();
    }

    internal static FileIndexMaintenanceOptions LoadDefault()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LuvLetter", "Index", "maintenance.json");
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<FileIndexMaintenanceOptions>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("Index maintenance configuration is empty.");
                loaded.Validate();
                return loaded.UpgradeLegacyIgnoreDefaults();
            }

            var defaults = new FileIndexMaintenanceOptions();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Create only; never overwrite a configuration supplied by another process.
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(file, defaults, new JsonSerializerOptions { WriteIndented = true });
            return defaults;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or ArgumentException or NotSupportedException)
        {
            // Falling back to an unrestricted scope could expose full-ignored entries.
            Console.Error.WriteLine($"[Index][configuration-error] Indexing paused until maintenance configuration is fixed: {exception.Message}");
            return new FileIndexMaintenanceOptions { IsAvailable = false };
        }
    }

    internal FileIndexMaintenanceOptions UpgradeLegacyIgnoreDefaults()
    {
        var names = FileIndexIgnoreDefaults.UpgradeDirectoryNames(IgnoreRebuildDirectoryNames);
        var caches = FileIndexIgnoreDefaults.UpgradeCacheDirectories(IgnoreRebuildCacheDirectories);
        if (ReferenceEquals(names, IgnoreRebuildDirectoryNames)
            && ReferenceEquals(caches, IgnoreRebuildCacheDirectories))
        {
            return this;
        }

        var upgraded = new FileIndexMaintenanceOptions
        {
            RefreshIntervalSeconds = RefreshIntervalSeconds,
            TriggerCooldownSeconds = TriggerCooldownSeconds,
            FullIgnorePaths = FullIgnorePaths,
            IsAvailable = IsAvailable,
            IgnoreRebuildDirectories = IgnoreRebuildDirectories,
            IgnoreRebuildDirectoryNames = names,
            IgnoreRebuildCacheDirectories = caches,
        };
        upgraded.Validate();
        Console.WriteLine("[Index][configuration] Previous default ignore lists upgraded in memory | user_file=unchanged");
        return upgraded;
    }
}

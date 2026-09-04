using System.IO;
using System.Text.Json;

namespace LuvLetter.Platform.Indexing;

internal sealed class FileIndexMaintenanceOptions
{
    public int RefreshIntervalSeconds { get; init; } = 360;

    public int TriggerCooldownSeconds { get; init; } = 60;

    public string[] IgnoreRebuildDirectories { get; init; } =
    [
        Path.GetTempPath(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LuvLetter", "Index"),
    ];

    // Exact directory components, not globs or repository-root exclusions.
    // Initializers also supply defaults when older JSON omits these new fields.
    public string[] IgnoreRebuildDirectoryNames { get; init; } =
    [
        ".git", ".hg", ".svn", ".vs", ".idea",
        "node_modules", ".pnpm-store", ".yarn",
        "bin", "obj", "build", "dist", "target", "coverage", "TestResults",
        ".next", ".nuxt", ".output", ".svelte-kit", ".angular", ".turbo", ".parcel-cache",
        ".venv", "venv", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox",
        ".gradle", ".dart_tool",
    ];

    public string[] IgnoreRebuildCacheDirectories { get; init; } =
    [
        "%USERPROFILE%\\.nuget\\packages",
        "%USERPROFILE%\\.m2\\repository",
        "%USERPROFILE%\\.cargo\\registry",
        "%USERPROFILE%\\.cargo\\git",
        "%LOCALAPPDATA%\\npm-cache",
        "%LOCALAPPDATA%\\pip\\Cache",
        "%LOCALAPPDATA%\\Yarn\\Cache",
        "%LOCALAPPDATA%\\pnpm\\store",
        "%LOCALAPPDATA%\\uv\\cache",
    ];

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
                return loaded;
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
            Console.Error.WriteLine($"Index maintenance configuration unavailable; using defaults: {exception.Message}");
            return new FileIndexMaintenanceOptions();
        }
    }
}

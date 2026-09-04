using System.Text.Json;
using LuvLetter.Platform.Indexing;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    // Capture the previously shipped configuration independently of the new
    // defaults helper so changing its legacy detection cannot change this fixture.
    private static readonly string[] LegacyIndexIgnoreNames =
    [
        ".git", ".hg", ".svn", ".vs", ".idea",
        "node_modules", ".pnpm-store", ".yarn",
        "bin", "obj", "build", "dist", "target", "coverage", "TestResults",
        ".next", ".nuxt", ".output", ".svelte-kit", ".angular", ".turbo", ".parcel-cache",
        ".venv", "venv", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox",
        ".gradle", ".dart_tool",
    ];

    private static readonly string[] LegacyIndexIgnoreCaches =
    [
        @"%USERPROFILE%\.nuget\packages",
        @"%USERPROFILE%\.m2\repository",
        @"%USERPROFILE%\.cargo\registry",
        @"%USERPROFILE%\.cargo\git",
        @"%LOCALAPPDATA%\npm-cache",
        @"%LOCALAPPDATA%\pip\Cache",
        @"%LOCALAPPDATA%\Yarn\Cache",
        @"%LOCALAPPDATA%\pnpm\store",
        @"%LOCALAPPDATA%\uv\cache",
    ];

    private static Task TestIndexIgnoreDefaults()
    {
        var defaults = new FileIndexMaintenanceOptions();
        defaults.Validate();
        var names = defaults.IgnoreRebuildDirectoryNames;
        var caches = defaults.IgnoreRebuildCacheDirectories;
        Assert.True(names.Length is > 0 and <= 128);
        Assert.True(caches.Length is > 0 and <= 128);
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(caches.Length, caches.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(names.All(name => name.IndexOfAny(['*', '?', '/', '\\']) < 0),
            "Default directory names must be exact components without wildcard or path syntax.");
        Assert.True(caches.All(path => path.IndexOfAny(['*', '?']) < 0),
            "Default cache scopes must be exact paths without wildcard syntax.");
        Assert.True(LegacyIndexIgnoreNames.All(name => names.Contains(name, StringComparer.OrdinalIgnoreCase)));
        Assert.True(LegacyIndexIgnoreCaches.All(path => caches.Contains(path, StringComparer.OrdinalIgnoreCase)));
        foreach (var required in new[] { ".vscode", ".codex", ".claude", ".copilot", ".agents", ".pycache", "pycache" })
        {
            Assert.True(names.Contains(required, StringComparer.OrdinalIgnoreCase),
                $"The expanded defaults must include {required}.");
        }
        Assert.Empty(defaults.FullIgnorePaths, "Expanding ordinary ignores must not introduce full exclusions.");
        Assert.True(ReferenceEquals(defaults, defaults.UpgradeLegacyIgnoreDefaults()),
            "Current defaults must not trigger another migration.");
        return Task.CompletedTask;
    }

    private static Task TestLegacyIndexIgnoreUpgrade()
    {
        foreach (var reorderAndFoldCase in new[] { false, true })
        {
            var names = reorderAndFoldCase
                ? LegacyIndexIgnoreNames.Reverse().Select(name => name.ToUpperInvariant()).ToArray()
                : LegacyIndexIgnoreNames.ToArray();
            var caches = reorderAndFoldCase
                ? LegacyIndexIgnoreCaches.Reverse().Select(path => path.ToUpperInvariant()).ToArray()
                : LegacyIndexIgnoreCaches.ToArray();
            var original = new FileIndexMaintenanceOptions
            {
                RefreshIntervalSeconds = 900,
                TriggerCooldownSeconds = 120,
                IgnoreRebuildDirectories = [@"C:\Local Generated Files"],
                FullIgnorePaths = [@"C:\Private\secret.txt", @"\\server\share\private"],
                IsAvailable = false,
                IgnoreRebuildDirectoryNames = names,
                IgnoreRebuildCacheDirectories = caches,
            };
            original.Validate();
            var originalJson = JsonSerializer.Serialize(original);
            var upgraded = original.UpgradeLegacyIgnoreDefaults();
            Assert.False(ReferenceEquals(original, upgraded));
            Assert.SequenceEqual(FileIndexIgnoreDefaults.CreateDirectoryNames(), upgraded.IgnoreRebuildDirectoryNames);
            Assert.SequenceEqual(FileIndexIgnoreDefaults.CreateCacheDirectories(), upgraded.IgnoreRebuildCacheDirectories);
            Assert.Equal(900, upgraded.RefreshIntervalSeconds);
            Assert.Equal(120, upgraded.TriggerCooldownSeconds);
            Assert.False(upgraded.IsAvailable);
            Assert.SequenceEqual(original.IgnoreRebuildDirectories, upgraded.IgnoreRebuildDirectories);
            Assert.SequenceEqual(original.FullIgnorePaths, upgraded.FullIgnorePaths);
            Assert.Equal(originalJson, JsonSerializer.Serialize(original),
                "An in-memory migration must not mutate the supplied configuration.");
            Assert.True(ReferenceEquals(names, original.IgnoreRebuildDirectoryNames));
            Assert.True(ReferenceEquals(caches, original.IgnoreRebuildCacheDirectories));
            Assert.True(ReferenceEquals(upgraded, upgraded.UpgradeLegacyIgnoreDefaults()),
                "Repeating a completed migration must be a no-op.");
        }
        return Task.CompletedTask;
    }

    private static Task TestCustomIndexIgnoreConfiguration()
    {
        string[][] customNames =
        [
            [.. LegacyIndexIgnoreNames, "my-generated-data"],
            LegacyIndexIgnoreNames.Skip(1).ToArray(),
            [],
        ];
        string[][] customCaches =
        [
            [.. LegacyIndexIgnoreCaches, @"C:\Custom Package Cache"],
            LegacyIndexIgnoreCaches.Skip(1).ToArray(),
            [],
        ];
        for (var index = 0; index < customNames.Length; index++)
        {
            var bothCustomized = new FileIndexMaintenanceOptions
            {
                IgnoreRebuildDirectoryNames = customNames[index],
                IgnoreRebuildCacheDirectories = customCaches[index],
            };
            bothCustomized.Validate();
            Assert.True(ReferenceEquals(bothCustomized, bothCustomized.UpgradeLegacyIgnoreDefaults()),
                "User additions, removals, and explicit empty lists must not be replaced.");

            var legacyCaches = new FileIndexMaintenanceOptions
            {
                IgnoreRebuildDirectoryNames = customNames[index],
                IgnoreRebuildCacheDirectories = LegacyIndexIgnoreCaches.ToArray(),
            }.UpgradeLegacyIgnoreDefaults();
            Assert.True(ReferenceEquals(customNames[index], legacyCaches.IgnoreRebuildDirectoryNames),
                "Upgrading legacy cache paths must preserve the custom name list exactly.");
            Assert.SequenceEqual(FileIndexIgnoreDefaults.CreateCacheDirectories(), legacyCaches.IgnoreRebuildCacheDirectories);

            var legacyNames = new FileIndexMaintenanceOptions
            {
                IgnoreRebuildDirectoryNames = LegacyIndexIgnoreNames.ToArray(),
                IgnoreRebuildCacheDirectories = customCaches[index],
            }.UpgradeLegacyIgnoreDefaults();
            Assert.True(ReferenceEquals(customCaches[index], legacyNames.IgnoreRebuildCacheDirectories),
                "Upgrading legacy names must preserve the custom cache list exactly.");
            Assert.SequenceEqual(FileIndexIgnoreDefaults.CreateDirectoryNames(), legacyNames.IgnoreRebuildDirectoryNames);
        }
        return Task.CompletedTask;
    }
}

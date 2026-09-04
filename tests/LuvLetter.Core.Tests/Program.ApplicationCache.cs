using LuvLetter.Core.Application;
using LuvLetter.Platform.Applications;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static async Task TestApplicationPartitionCacheFallback()
    {
        var directory = Path.Combine(Path.GetTempPath(), "LuvLetter.Tests", Guid.NewGuid().ToString("N"));
        const string source = "test:applications";
        const string fingerprint = "test-scope";
        try
        {
            var cache = new ApplicationPartitionCache(directory);
            var first = ApplicationPartitionCache.CreateSnapshot(source, fingerprint, 1,
            [
                new ApplicationEntry(source + ":first", "First", [], ApplicationLaunchKind.Executable,
                    @"C:\Apps\First.exe", Source: source),
            ]);
            var firstGeneration = await cache.SaveAsync(first, null, CancellationToken.None);

            var second = ApplicationPartitionCache.CreateSnapshot(source, fingerprint, 2,
            [
                new ApplicationEntry(source + ":second", "Second", [], ApplicationLaunchKind.Executable,
                    @"C:\Apps\Second.exe", Source: source),
            ]);
            _ = await cache.SaveAsync(second, firstGeneration, CancellationToken.None);

            var partitions = Path.Combine(directory, "v2", "partitions");
            var primarySnapshot = Directory.GetFiles(partitions, "*.snapshot.json").Single();
            await File.WriteAllTextAsync(primarySnapshot, "corrupt");
            var fallback = await cache.LoadAsync(source, fingerprint, CancellationToken.None);
            Assert.Equal("backup", fallback.Result);
            Assert.Equal(1L, Assert.NotNull(fallback.Snapshot).Generation,
                "A corrupt primary application cache must retain the preceding disk generation.");

            var third = ApplicationPartitionCache.CreateSnapshot(source, fingerprint, 3,
            [
                new ApplicationEntry(source + ":third", "Third", [], ApplicationLaunchKind.Executable,
                    @"C:\Apps\Third.exe", Source: source),
            ]);
            _ = await cache.SaveAsync(third, Assert.NotNull(fallback.Generation), CancellationToken.None);
            await File.WriteAllTextAsync(primarySnapshot, "corrupt-again");
            fallback = await cache.LoadAsync(source, fingerprint, CancellationToken.None);
            Assert.Equal("backup", fallback.Result);
            Assert.Equal(1L, Assert.NotNull(fallback.Snapshot).Generation,
                "Saving after backup recovery must not overwrite the trusted recovery generation.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

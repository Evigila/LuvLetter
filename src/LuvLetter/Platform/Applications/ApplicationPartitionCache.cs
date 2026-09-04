using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LuvLetter.Core.Application;

namespace LuvLetter.Platform.Applications;

internal sealed class ApplicationPartitionCache
{
    private const int CacheVersion = 2;
    private const int MaximumManifestBytes = 64 * 1024;
    private const int MaximumSnapshotBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    private readonly string directory;

    internal ApplicationPartitionCache(string dataDirectory)
    {
        directory = Path.Combine(dataDirectory, "v2", "partitions");
    }

    internal async Task<ApplicationPartitionCacheLoadResult> LoadAsync(
        string sourceId,
        string scopeFingerprint,
        CancellationToken cancellationToken)
    {
        var paths = Paths(sourceId);
        var sawCandidate = false;
        foreach (var pair in new[]
        {
            (paths.Manifest, paths.Snapshot, "primary"),
            (paths.Manifest + ".bak", paths.Snapshot + ".bak", "backup"),
        })
        {
            try
            {
                if (!File.Exists(pair.Item1) && !File.Exists(pair.Item2)) continue;
                sawCandidate = true;
                var manifestBytes = await ReadBoundedAsync(pair.Item1, MaximumManifestBytes, cancellationToken)
                    .ConfigureAwait(false);
                var snapshotBytes = await ReadBoundedAsync(pair.Item2, MaximumSnapshotBytes, cancellationToken)
                    .ConfigureAwait(false);
                var manifest = JsonSerializer.Deserialize<ApplicationPartitionManifest>(manifestBytes, JsonOptions);
                if (manifest is null || manifest.Version != CacheVersion
                    || manifest.SourceId != sourceId || manifest.ScopeFingerprint != scopeFingerprint
                    || manifest.Generation < 0 || manifest.Generation == long.MaxValue || manifest.EntryCount < 0
                    || manifest.SnapshotChecksum != Checksum(snapshotBytes)) continue;
                var snapshot = JsonSerializer.Deserialize<ApplicationPartitionSnapshot>(snapshotBytes, JsonOptions);
                if (snapshot is null || snapshot.Version != CacheVersion
                    || snapshot.SourceId != sourceId || snapshot.ScopeFingerprint != scopeFingerprint
                    || snapshot.Generation != manifest.Generation || snapshot.Entries is null
                    || snapshot.Entries.Length != manifest.EntryCount) continue;
                return new(new(snapshot, manifestBytes, snapshotBytes), pair.Item3);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or JsonException or ArgumentException or NotSupportedException)
            {
                sawCandidate = true;
            }
        }
        return new(null, sawCandidate ? "incompatible" : "missing");
    }

    internal async Task<ApplicationPartitionCacheHit> SaveAsync(
        ApplicationPartitionSnapshot snapshot,
        ApplicationPartitionCacheHit? previous,
        CancellationToken cancellationToken)
    {
        var snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (snapshotBytes.Length <= 0 || snapshotBytes.Length > MaximumSnapshotBytes)
            throw new InvalidDataException("Application partition snapshot exceeds the size limit.");
        var manifest = new ApplicationPartitionManifest(CacheVersion, snapshot.SourceId,
            snapshot.ScopeFingerprint, snapshot.Generation, snapshot.Entries.Length, Checksum(snapshotBytes));
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (manifestBytes.Length <= 0 || manifestBytes.Length > MaximumManifestBytes)
            throw new InvalidDataException("Application partition manifest exceeds the size limit.");

        Directory.CreateDirectory(directory);
        var paths = Paths(snapshot.SourceId);
        var backup = previous ?? new ApplicationPartitionCacheHit(snapshot, manifestBytes, snapshotBytes);
        await AtomicWriteAsync(paths.Snapshot + ".bak", backup.SnapshotBytes, cancellationToken).ConfigureAwait(false);
        await AtomicWriteAsync(paths.Manifest + ".bak", backup.ManifestBytes, cancellationToken).ConfigureAwait(false);
        await AtomicWriteAsync(paths.Snapshot, snapshotBytes, cancellationToken).ConfigureAwait(false);
        await AtomicWriteAsync(paths.Manifest, manifestBytes, cancellationToken).ConfigureAwait(false);
        return new(snapshot, manifestBytes, snapshotBytes);
    }

    internal static ApplicationPartitionSnapshot CreateSnapshot(
        string sourceId, string scopeFingerprint, long generation, ApplicationEntry[] entries) =>
        new(CacheVersion, sourceId, scopeFingerprint, generation, entries);

    private (string Manifest, string Snapshot) Paths(string sourceId)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceId)));
        return (Path.Combine(directory, key + ".manifest.json"),
            Path.Combine(directory, key + ".snapshot.json"));
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("Application partition cache has an invalid size.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static async Task AtomicWriteAsync(
        string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string Checksum(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));
}

internal sealed record ApplicationPartitionCacheLoadResult(
    ApplicationPartitionCacheHit? Hit,
    string Result);

internal sealed record ApplicationPartitionCacheHit(
    ApplicationPartitionSnapshot Snapshot,
    byte[] ManifestBytes,
    byte[] SnapshotBytes);

internal sealed record ApplicationPartitionSnapshot(
    int Version,
    string SourceId,
    string ScopeFingerprint,
    long Generation,
    ApplicationEntry[] Entries);

internal sealed record ApplicationPartitionManifest(
    int Version,
    string SourceId,
    string ScopeFingerprint,
    long Generation,
    int EntryCount,
    string SnapshotChecksum);

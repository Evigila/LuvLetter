namespace LuvLetter.Core.Application.IndexPartitions;

public interface IFullIgnorePolicy
{
    bool IsFullyIgnored(string normalizedPath);
}

public readonly record struct OwnershipResolution(PartitionId? Owner, bool FullyIgnored);

public sealed class OwnershipMap
{
    private readonly (PartitionId Id, string Root)[] roots;
    private readonly IFullIgnorePolicy? fullIgnorePolicy;

    public OwnershipMap(
        long ownershipEpoch,
        IEnumerable<PartitionDescriptor> partitions,
        IFullIgnorePolicy? fullIgnorePolicy = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ownershipEpoch);
        ArgumentNullException.ThrowIfNull(partitions);
        OwnershipEpoch = ownershipEpoch;
        this.fullIgnorePolicy = fullIgnorePolicy;
        var descriptors = partitions.ToArray();
        if (descriptors.Any(partition => partition is null))
            throw new ArgumentException("An ownership map cannot contain null partitions.", nameof(partitions));
        roots = descriptors.Where(partition => partition.FilesystemRoot is not null)
            .Select(partition => (partition.Id, NormalizePath(partition.FilesystemRoot!)))
            .OrderByDescending(item => item.Item2.Length)
            .ThenBy(item => item.Id)
            .ToArray();
        if (roots.Select(item => item.Root).Distinct(StringComparer.OrdinalIgnoreCase).Count() != roots.Length)
            throw new ArgumentException("Filesystem ownership roots must be unique.", nameof(partitions));
    }

    public long OwnershipEpoch { get; }

    public OwnershipResolution ResolveFilesystemPath(string path)
    {
        var normalized = NormalizePath(path);
        if (fullIgnorePolicy?.IsFullyIgnored(normalized) == true)
            return new OwnershipResolution(null, true);
        foreach (var candidate in roots)
            if (IsSameOrChild(candidate.Root, normalized))
                return new OwnershipResolution(candidate.Id, false);
        return new OwnershipResolution(null, false);
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Ownership paths must be fully qualified.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsSameOrChild(string root, string path) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}

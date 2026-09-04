namespace LuvLetter.Core.Application.IndexPartitions;

public readonly record struct PartitionId : IComparable<PartitionId>
{
    public PartitionId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || !IsAlphaNumeric(value[0]) || !IsAlphaNumeric(value[^1])
            || value.Any(character => !IsAlphaNumeric(character) && character is not ('.' or '_' or ':' or '-')))
        {
            throw new ArgumentException(
                "Partition IDs must be 1-128 lowercase ASCII characters, start and end with a letter or digit, and contain only '.', '_', ':', or '-' separators.",
                nameof(value));
        }
        Value = value;
    }

    public string Value { get; }

    public int CompareTo(PartitionId other) => StringComparer.Ordinal.Compare(Value, other.Value);

    public override string ToString() => Value ?? string.Empty;

    internal void Validate(string parameterName)
    {
        if (Value is null) throw new ArgumentException("A partition ID is required.", parameterName);
    }

    private static bool IsAlphaNumeric(char value) => value is >= 'a' and <= 'z' or >= '0' and <= '9';
}

public enum IndexSourceKind
{
    Filesystem,
    Applications,
}

[Flags]
public enum EntityKind
{
    None = 0,
    File = 1,
    Directory = 2,
    Application = 4,
}

public enum MaintenanceTier
{
    Immediate,
    Regular,
    Deferred,
}

public enum ResourceLane
{
    Filesystem,
    Shell,
    Compute,
    Network,
}

public enum Availability
{
    Unknown,
    Available,
    Degraded,
    Unavailable,
}

public enum Freshness
{
    Unknown,
    Empty,
    Fresh,
    Dirty,
    Stale,
}

public enum RefreshState
{
    Idle,
    Queued,
    Running,
    Succeeded,
    Failed,
}

public sealed record PartitionDescriptor
{
    public PartitionDescriptor(
        PartitionId id,
        IndexSourceKind sourceKind,
        EntityKind entityKinds,
        MaintenanceTier maintenanceTier,
        ResourceLane resourceLane,
        int startupImportance,
        TimeSpan estimatedCost,
        string? filesystemRoot = null)
    {
        id.Validate(nameof(id));
        if (!Enum.IsDefined(sourceKind)) throw new ArgumentOutOfRangeException(nameof(sourceKind));
        if (entityKinds == EntityKind.None || (entityKinds & ~AllEntityKinds) != 0)
            throw new ArgumentOutOfRangeException(nameof(entityKinds));
        if (!Enum.IsDefined(maintenanceTier)) throw new ArgumentOutOfRangeException(nameof(maintenanceTier));
        if (!Enum.IsDefined(resourceLane)) throw new ArgumentOutOfRangeException(nameof(resourceLane));
        if (startupImportance is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(startupImportance));
        if (estimatedCost < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(estimatedCost));
        if (filesystemRoot is { Length: 0 }) throw new ArgumentException("A filesystem root cannot be empty.", nameof(filesystemRoot));

        Id = id;
        SourceKind = sourceKind;
        EntityKinds = entityKinds;
        MaintenanceTier = maintenanceTier;
        ResourceLane = resourceLane;
        StartupImportance = startupImportance;
        EstimatedCost = estimatedCost;
        FilesystemRoot = filesystemRoot;
    }

    private const EntityKind AllEntityKinds = EntityKind.File | EntityKind.Directory | EntityKind.Application;

    public PartitionId Id { get; }
    public IndexSourceKind SourceKind { get; }
    public EntityKind EntityKinds { get; }
    public MaintenanceTier MaintenanceTier { get; }
    public ResourceLane ResourceLane { get; }
    public int StartupImportance { get; }
    public TimeSpan EstimatedCost { get; }
    public string? FilesystemRoot { get; }
}

public sealed class PartitionManifest
{
    public PartitionManifest(long ownershipEpoch, IEnumerable<PartitionDescriptor> partitions)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentOutOfRangeException.ThrowIfNegative(ownershipEpoch);
        OwnershipEpoch = ownershipEpoch;
        var copied = partitions.ToArray();
        if (copied.Any(partition => partition is null))
            throw new ArgumentException("A manifest cannot contain null partitions.", nameof(partitions));
        if (copied.Select(partition => partition.Id).Distinct().Count() != copied.Length)
            throw new ArgumentException("Partition IDs must be unique within a manifest.", nameof(partitions));
        Partitions = Array.AsReadOnly(copied);
    }

    public long OwnershipEpoch { get; }
    public IReadOnlyList<PartitionDescriptor> Partitions { get; }
}

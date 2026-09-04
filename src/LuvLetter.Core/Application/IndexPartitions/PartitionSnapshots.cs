namespace LuvLetter.Core.Application.IndexPartitions;

/// <summary>Implementations must be immutable after publication.</summary>
public interface IPartitionSnapshot
{
    PartitionId PartitionId { get; }
    long OwnershipEpoch { get; }
    long Generation { get; }
    DateTimeOffset PublishedAt { get; }
}

public sealed class SnapshotSet
{
    private readonly IReadOnlyDictionary<PartitionId, IPartitionSnapshot> snapshots;

    public SnapshotSet(long ownershipEpoch, IEnumerable<IPartitionSnapshot> snapshots)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ownershipEpoch);
        ArgumentNullException.ThrowIfNull(snapshots);
        OwnershipEpoch = ownershipEpoch;
        var byId = new Dictionary<PartitionId, IPartitionSnapshot>();
        foreach (var snapshot in snapshots)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            snapshot.PartitionId.Validate(nameof(snapshots));
            if (snapshot.OwnershipEpoch != ownershipEpoch)
                throw new ArgumentException("Every snapshot must match the set's ownership epoch.", nameof(snapshots));
            if (snapshot.Generation < 0)
                throw new ArgumentException("Snapshot generations cannot be negative.", nameof(snapshots));
            if (!byId.TryAdd(snapshot.PartitionId, snapshot))
                throw new ArgumentException("A snapshot set cannot contain duplicate partition IDs.", nameof(snapshots));
        }
        this.snapshots = byId;
    }

    public long OwnershipEpoch { get; }

    public PartitionReadView Capture() => new(OwnershipEpoch, snapshots);
}

public sealed class PartitionReadView
{
    private readonly IReadOnlyDictionary<PartitionId, IPartitionSnapshot> snapshots;
    private readonly IReadOnlyCollection<PartitionId> partitionIds;

    internal PartitionReadView(long ownershipEpoch, IReadOnlyDictionary<PartitionId, IPartitionSnapshot> snapshots)
    {
        OwnershipEpoch = ownershipEpoch;
        this.snapshots = snapshots;
        partitionIds = snapshots.Keys.ToArray();
    }

    public long OwnershipEpoch { get; }
    public IReadOnlyCollection<PartitionId> PartitionIds => partitionIds;

    public bool TryGet(PartitionId id, out IPartitionSnapshot? snapshot)
    {
        id.Validate(nameof(id));
        return snapshots.TryGetValue(id, out snapshot);
    }

    public TSnapshot GetRequired<TSnapshot>(PartitionId id) where TSnapshot : class, IPartitionSnapshot
    {
        if (!TryGet(id, out var snapshot)) throw new KeyNotFoundException($"Partition '{id}' is not published.");
        return snapshot as TSnapshot
            ?? throw new InvalidCastException($"Partition '{id}' does not contain a {typeof(TSnapshot).Name} snapshot.");
    }
}

public interface IPartitionRefreshRequester
{
    void RequestRefresh(PartitionId partitionId, bool force = false);
}

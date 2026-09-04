using LuvLetter.Core.Application.IndexPartitions;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static Task TestIndexPartitionMetadata()
    {
        Assert.Equal("filesystem:user-profile", new PartitionId("filesystem:user-profile").Value);
        foreach (var invalid in new[] { "", " ", "Upper", "-leading", "trailing-", "with/slash", "two words", "a*" })
            Assert.Throws<ArgumentException>(() => _ = new PartitionId(invalid));
        Assert.Throws<ArgumentException>(() => _ = new PartitionId(new string('a', 129)));

        var first = TestPartition("filesystem:user-profile", @"C:\Users\Owner");
        var second = TestPartition("applications:start-menu", null, IndexSourceKind.Applications,
            EntityKind.Application, ResourceLane.Shell);
        var source = new List<PartitionDescriptor> { first, second };
        var manifest = new PartitionManifest(17, source);
        source.Clear();
        Assert.Equal(17L, manifest.OwnershipEpoch);
        Assert.Equal(2, manifest.Partitions.Count, "A manifest must snapshot its source collection.");
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PartitionManifest(-1, []));
        Assert.Throws<ArgumentException>(() => _ = new PartitionManifest(0, [first, first]));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PartitionDescriptor(first.Id,
            IndexSourceKind.Filesystem, EntityKind.None, MaintenanceTier.Regular, ResourceLane.Filesystem,
            100, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PartitionDescriptor(first.Id,
            IndexSourceKind.Filesystem, EntityKind.File, MaintenanceTier.Regular, ResourceLane.Filesystem,
            1001, TimeSpan.Zero));
        return Task.CompletedTask;
    }

    private static Task TestIndexPartitionOwnership()
    {
        var parent = TestPartition("filesystem:user", @"C:\Users\Owner");
        var projects = TestPartition("filesystem:projects", @"C:\Users\Owner\Projects");
        var delegated = TestPartition("filesystem:delegated", @"C:\Users\Owner\Projects\Delegated");
        var ignored = new TestFullIgnorePolicy(@"C:\Users\Owner\Projects\Private");
        var ownership = new OwnershipMap(23, [parent, delegated, projects], ignored);

        Assert.Equal(23L, ownership.OwnershipEpoch);
        Assert.True(ownership.ResolveFilesystemPath(@"c:\USERS\owner\PROJECTS\delegated\child.txt").Owner == delegated.Id);
        Assert.True(ownership.ResolveFilesystemPath(@"C:\Users\Owner\Projects\readme.md").Owner == projects.Id,
            "The longest matching nested root must own its ordinary delegated subtree.");
        Assert.True(ownership.ResolveFilesystemPath(@"C:\Users\Owner\Pictures\photo.jpg").Owner == parent.Id);
        Assert.False(ownership.ResolveFilesystemPath(@"C:\Users\OwnerElse\file.txt").Owner.HasValue,
            "Filesystem roots must match complete path components.");
        var excluded = ownership.ResolveFilesystemPath(@"C:\Users\Owner\Projects\Private\secret.txt");
        Assert.True(excluded.FullyIgnored && !excluded.Owner.HasValue,
            "Full ignore must take precedence over the most specific owner.");
        Assert.Throws<ArgumentException>(() => ownership.ResolveFilesystemPath("relative.txt"));
        Assert.Throws<ArgumentException>(() => _ = new OwnershipMap(1,
            [parent, TestPartition("filesystem:duplicate", @"c:\users\owner")]));
        return Task.CompletedTask;
    }

    private static Task TestIndexPartitionScheduling()
    {
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var standard = TestPartition("filesystem:standard", @"C:\Data", startupImportance: 100,
            estimatedCost: TimeSpan.FromMinutes(20));
        PartitionScheduleState State(PartitionDescriptor descriptor,
            DateTimeOffset? due = null, DateTimeOffset? dirty = null, DateTimeOffset? serviced = null) =>
            new(descriptor, Availability.Available, Freshness.Dirty, RefreshState.Queued, due, dirty, serviced);

        var baseline = State(standard, now, now, now);
        Assert.True(double.IsFinite(PartitionSchedulerPriority.Calculate(baseline, now)));
        Assert.True(PartitionSchedulerPriority.Calculate(State(standard, now.AddHours(-2), now, now), now)
            > PartitionSchedulerPriority.Calculate(baseline, now), "Overdue age must increase priority.");
        Assert.True(PartitionSchedulerPriority.Calculate(State(standard, now, now.AddHours(-2), now), now)
            > PartitionSchedulerPriority.Calculate(baseline, now), "Dirty age must increase priority.");
        Assert.True(PartitionSchedulerPriority.Calculate(State(standard, now, now, now.AddHours(-2)), now)
            > PartitionSchedulerPriority.Calculate(baseline, now), "Starvation age must increase priority.");
        var expensive = TestPartition("filesystem:expensive", @"C:\Expensive", startupImportance: 100,
            estimatedCost: TimeSpan.FromHours(2));
        Assert.True(PartitionSchedulerPriority.Calculate(baseline, now)
            > PartitionSchedulerPriority.Calculate(State(expensive, now, now, now), now),
            "Estimated work cost must reduce dispatch priority.");
        var startup = TestPartition("filesystem:startup", @"C:\Startup", startupImportance: 900,
            estimatedCost: TimeSpan.FromMinutes(20));
        Assert.True(PartitionSchedulerPriority.Calculate(State(startup, now, now, now), now)
            > PartitionSchedulerPriority.Calculate(baseline, now));

        var maximumAging = State(standard, DateTimeOffset.MinValue, DateTimeOffset.MinValue, DateTimeOffset.MinValue);
        var maximumScore = PartitionSchedulerPriority.Calculate(maximumAging, DateTimeOffset.MaxValue);
        Assert.True(double.IsFinite(maximumScore), "Extreme timestamps must remain finite and bounded.");
        Assert.Equal(maximumScore, PartitionSchedulerPriority.Calculate(maximumAging, DateTimeOffset.MaxValue),
            "Priority calculation must be stable for the same state and timestamp.");
        var alpha = State(TestPartition("filesystem:alpha", @"C:\Alpha"), now, now, now);
        var beta = State(TestPartition("filesystem:beta", @"C:\Beta"), now, now, now);
        Assert.True(PartitionSchedulerPriority.Compare(alpha, beta, now) < 0,
            "Equal scores must use the partition ID as a deterministic tie breaker.");
        return Task.CompletedTask;
    }

    private static Task TestIndexPartitionReadViews()
    {
        var first = new TestPartitionSnapshot(new PartitionId("filesystem:first"), 41, 1);
        var second = new TestPartitionSnapshot(new PartitionId("applications:start-menu"), 41, 99);
        var input = new List<IPartitionSnapshot> { first, second };
        var set = new SnapshotSet(41, input);
        input.Clear();
        var view = set.Capture();
        Assert.Equal(41L, view.OwnershipEpoch);
        Assert.Equal(2, view.PartitionIds.Count);
        Assert.True(view.TryGet(first.PartitionId, out var found) && ReferenceEquals(first, found));
        Assert.True(ReferenceEquals(second, view.GetRequired<TestPartitionSnapshot>(second.PartitionId)));
        Assert.Equal(1L, first.Generation);
        Assert.Equal(99L, second.Generation, "Snapshot generations must remain independent within one ownership epoch.");
        Assert.Throws<KeyNotFoundException>(() => view.GetRequired<TestPartitionSnapshot>(new PartitionId("missing:id")));
        Assert.Throws<ArgumentException>(() => _ = new SnapshotSet(41,
            [first, new TestPartitionSnapshot(second.PartitionId, 42, 1)]));
        Assert.Throws<ArgumentException>(() => _ = new SnapshotSet(41, [first, first]));

        var requests = new TestPartitionRefreshRequester();
        requests.RequestRefresh(second.PartitionId, force: true);
        Assert.Equal((second.PartitionId, true), requests.Requests.Single());
        return Task.CompletedTask;
    }

    private static PartitionDescriptor TestPartition(
        string id,
        string? root,
        IndexSourceKind source = IndexSourceKind.Filesystem,
        EntityKind entities = EntityKind.File | EntityKind.Directory,
        ResourceLane lane = ResourceLane.Filesystem,
        int startupImportance = 100,
        TimeSpan? estimatedCost = null) =>
        new(new PartitionId(id), source, entities, MaintenanceTier.Regular, lane,
            startupImportance, estimatedCost ?? TimeSpan.FromMinutes(1), root);

    private sealed class TestFullIgnorePolicy(string root) : IFullIgnorePolicy
    {
        private readonly string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        public bool IsFullyIgnored(string normalizedPath) =>
            normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TestPartitionSnapshot(
        PartitionId PartitionId,
        long OwnershipEpoch,
        long Generation) : IPartitionSnapshot
    {
        public DateTimeOffset PublishedAt { get; } = DateTimeOffset.UnixEpoch;
    }

    private sealed class TestPartitionRefreshRequester : IPartitionRefreshRequester
    {
        public List<(PartitionId PartitionId, bool Force)> Requests { get; } = [];
        public void RequestRefresh(PartitionId partitionId, bool force = false)
        {
            Requests.Add((partitionId, force));
        }
    }
}

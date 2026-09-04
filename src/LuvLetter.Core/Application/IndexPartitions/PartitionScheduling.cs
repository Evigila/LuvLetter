namespace LuvLetter.Core.Application.IndexPartitions;

public sealed record PartitionScheduleState(
    PartitionDescriptor Descriptor,
    Availability Availability,
    Freshness Freshness,
    RefreshState RefreshState,
    DateTimeOffset? DueAt = null,
    DateTimeOffset? DirtySince = null,
    DateTimeOffset? LastServicedAt = null);

public static class PartitionSchedulerPriority
{
    private const double MaximumAgeMinutes = 1440;

    public static double Calculate(PartitionScheduleState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(state.Descriptor);
        ValidateState(state);
        var overdue = state.DueAt is { } due ? BoundedAgeMinutes(now - due) : 0;
        var dirtyAge = state.DirtySince is { } dirty ? BoundedAgeMinutes(now - dirty) : 0;
        var starvation = state.LastServicedAt is { } serviced
            ? BoundedAgeMinutes(now - serviced) : MaximumAgeMinutes;
        var estimatedCost = Math.Min(MaximumAgeMinutes, state.Descriptor.EstimatedCost.TotalMinutes);
        var score = state.Descriptor.StartupImportance + overdue + dirtyAge + starvation - estimatedCost;
        return double.IsFinite(score) ? score : 0;
    }

    // Negative means left should be dispatched first. ID order makes equal scores stable.
    public static int Compare(PartitionScheduleState left, PartitionScheduleState right, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var score = Calculate(right, now).CompareTo(Calculate(left, now));
        return score != 0 ? score : left.Descriptor.Id.CompareTo(right.Descriptor.Id);
    }

    private static double BoundedAgeMinutes(TimeSpan age) =>
        Math.Clamp(age.TotalMinutes, 0, MaximumAgeMinutes);

    private static void ValidateState(PartitionScheduleState state)
    {
        if (!Enum.IsDefined(state.Availability)) throw new ArgumentOutOfRangeException(nameof(state));
        if (!Enum.IsDefined(state.Freshness)) throw new ArgumentOutOfRangeException(nameof(state));
        if (!Enum.IsDefined(state.RefreshState)) throw new ArgumentOutOfRangeException(nameof(state));
    }
}

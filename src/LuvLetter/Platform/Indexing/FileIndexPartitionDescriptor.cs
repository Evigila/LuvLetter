namespace LuvLetter.Platform.Indexing;

internal enum FileIndexMaintenanceTier : uint
{
    StartupCritical = 0,
    Normal = 1,
}

internal sealed record FileIndexPartitionDescriptor(
    string Id,
    string Root,
    string[] DelegatedSubtrees,
    FileIndexMaintenanceTier Tier,
    int RefreshAgeSeconds,
    int AutomaticGapSeconds);

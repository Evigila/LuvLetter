namespace LuvLetter.Core.Application;

/// <summary>
/// Controls the bounded candidate list produced for each editor revision.
/// </summary>
public sealed class InputCandidateOptions
{
    public const int DefaultFileCandidateCount = 5;
    public const int DefaultTotalCandidateCount = 6;
    public const int MaximumCandidateCount = 32;

    public int FileCandidateCount { get; init; } = DefaultFileCandidateCount;

    public int TotalCandidateCount { get; init; } = DefaultTotalCandidateCount;

    public string CommandDescription { get; init; } = "Command";

    public string GlobalSearchLabel { get; init; } = "Global Search";

    public string GlobalSearchDescription { get; init; } = "Search all indexed files";

    public string GlobalSearchUnavailableMessage { get; init; } = "全局搜索功能尚未实现。";

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(FileCandidateCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(TotalCandidateCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            FileCandidateCount,
            MaximumCandidateCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            TotalCandidateCount,
            MaximumCandidateCount);
        ArgumentException.ThrowIfNullOrWhiteSpace(CommandDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(GlobalSearchLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(GlobalSearchDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(GlobalSearchUnavailableMessage);
    }
}

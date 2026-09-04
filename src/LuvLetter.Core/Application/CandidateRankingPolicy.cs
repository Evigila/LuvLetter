namespace LuvLetter.Core.Application;

public enum SearchCandidateSource { Application, File, Directory }

public sealed record CandidateRankingContext(
    string Identity, string Query, SearchCandidateSource Source, int MatchScore);

public interface ICandidateRankingPolicy
{
    double Score(CandidateRankingContext candidate);
}

// A future usage/pinning service can supply boosts without changing discovery,
// caches, activation targets, or the candidate coordinator's merge algorithm.
public interface ICandidatePriorityProvider
{
    double GetBoost(CandidateRankingContext candidate);
}

public sealed class CandidateRankingOptions
{
    public double ApplicationBias { get; init; } = 1000;
}

public sealed class DefaultCandidateRankingPolicy : ICandidateRankingPolicy
{
    private readonly CandidateRankingOptions options;
    private readonly ICandidatePriorityProvider? priorityProvider;

    public DefaultCandidateRankingPolicy(
        CandidateRankingOptions? options = null, ICandidatePriorityProvider? priorityProvider = null)
    {
        this.options = options ?? new CandidateRankingOptions();
        if (!double.IsFinite(this.options.ApplicationBias)) throw new ArgumentOutOfRangeException(nameof(options));
        this.priorityProvider = priorityProvider;
    }

    public double Score(CandidateRankingContext candidate)
    {
        var boost = priorityProvider?.GetBoost(candidate) ?? 0;
        if (!double.IsFinite(boost)) boost = 0;
        return (candidate.Source == SearchCandidateSource.Application ? options.ApplicationBias : 0)
            + candidate.MatchScore + boost;
    }

    public static int FileMatchScore(FileIndexMatch file, string query)
    {
        if (file.DisplayName.Equals(query, StringComparison.OrdinalIgnoreCase)) return 300;
        if (file.EntryKind == FileSystemEntryKind.File
            && Path.GetFileNameWithoutExtension(file.DisplayName).Equals(query, StringComparison.OrdinalIgnoreCase)) return 280;
        return file.DisplayName.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 100 : 0;
    }
}

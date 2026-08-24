using LuvLetter.Core.Features;

namespace LuvLetter.Core.Native;

/// <summary>
/// Owns the stable mapping between managed feature identifiers and opaque Native tokens.
/// Tokens are never reused after allocation, including when synchronization later fails.
/// </summary>
internal sealed class FeatureTokenRegistry
{
    private Dictionary<string, ulong> tokensByFeatureId = new(StringComparer.Ordinal);
    private ulong nextToken = 1;

    public FeatureTokenAssignment Prepare(IReadOnlyList<FeatureItemSnapshot> features)
    {
        ArgumentNullException.ThrowIfNull(features);

        var items = new FeatureTokenItem[features.Count];
        var featureIdsByToken = new Dictionary<ulong, string>(features.Count);
        var nextTokensByFeatureId = new Dictionary<string, ulong>(
            features.Count,
            StringComparer.Ordinal);

        for (var index = 0; index < features.Count; index++)
        {
            var feature = features[index];
            if (string.IsNullOrWhiteSpace(feature.Id))
            {
                throw new ArgumentException(
                    "A feature snapshot must have a non-empty identifier.",
                    nameof(features));
            }

            if (string.IsNullOrWhiteSpace(feature.DisplayName))
            {
                throw new ArgumentException(
                    "A feature snapshot must have a non-empty display name.",
                    nameof(features));
            }

            var featureId = feature.Id.Trim();
            if (nextTokensByFeatureId.ContainsKey(featureId))
            {
                throw new ArgumentException(
                    $"Feature identifier '{featureId}' occurs more than once.",
                    nameof(features));
            }

            var token = GetOrCreateToken(featureId);
            items[index] = new(token, featureId, feature.DisplayName);
            featureIdsByToken.Add(token, featureId);
            nextTokensByFeatureId.Add(featureId, token);
        }

        return new(items, featureIdsByToken, nextTokensByFeatureId);
    }

    public void Commit(FeatureTokenAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        tokensByFeatureId = new Dictionary<string, ulong>(
            assignment.TokensByFeatureId,
            StringComparer.Ordinal);
    }

    public static IReadOnlyDictionary<ulong, string> CreateTransitionMap(
        IReadOnlyDictionary<ulong, string> previousFeatureIds,
        IReadOnlyDictionary<ulong, string> nextFeatureIds)
    {
        foreach (var token in previousFeatureIds.Keys)
        {
            if (nextFeatureIds.ContainsKey(token))
            {
                continue;
            }

            var transition = new Dictionary<ulong, string>(
                previousFeatureIds.Count + nextFeatureIds.Count);
            foreach (var (previousToken, featureId) in previousFeatureIds)
            {
                transition.Add(previousToken, featureId);
            }

            foreach (var (nextToken, featureId) in nextFeatureIds)
            {
                transition[nextToken] = featureId;
            }

            return transition;
        }

        return nextFeatureIds;
    }

    private ulong GetOrCreateToken(string featureId)
    {
        if (tokensByFeatureId.TryGetValue(featureId, out var token))
        {
            return token;
        }

        if (nextToken == 0)
        {
            throw new InvalidOperationException("The feature token space has been exhausted.");
        }

        token = nextToken++;
        return token;
    }
}

internal sealed record FeatureTokenAssignment(
    IReadOnlyList<FeatureTokenItem> Items,
    IReadOnlyDictionary<ulong, string> FeatureIdsByToken,
    IReadOnlyDictionary<string, ulong> TokensByFeatureId);

internal sealed record FeatureTokenItem(
    ulong Token,
    string FeatureId,
    string DisplayName);

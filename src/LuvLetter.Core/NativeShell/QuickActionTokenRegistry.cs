using LuvLetter.Core.Modules.QuickActions;

namespace LuvLetter.Core.NativeShell;

/// <summary>
/// Owns the stable mapping between managed quick-action identifiers and opaque Native tokens.
/// Tokens are never reused after allocation, including when synchronization later fails.
/// </summary>
internal sealed class QuickActionTokenRegistry
{
    private Dictionary<string, ulong> tokensByQuickActionId = new(StringComparer.Ordinal);
    private ulong nextToken = 1;

    public QuickActionTokenAssignment Prepare(IReadOnlyList<QuickActionSnapshot> quickActions)
    {
        ArgumentNullException.ThrowIfNull(quickActions);

        var items = new QuickActionTokenItem[quickActions.Count];
        var quickActionIdsByToken = new Dictionary<ulong, string>(quickActions.Count);
        var nextTokensByQuickActionId = new Dictionary<string, ulong>(
            quickActions.Count,
            StringComparer.Ordinal);

        for (var index = 0; index < quickActions.Count; index++)
        {
            var quickAction = quickActions[index];
            if (string.IsNullOrWhiteSpace(quickAction.Id))
            {
                throw new ArgumentException(
                    "A quick-action snapshot must have a non-empty identifier.",
                    nameof(quickActions));
            }

            if (string.IsNullOrWhiteSpace(quickAction.DisplayName))
            {
                throw new ArgumentException(
                    "A quick-action snapshot must have a non-empty display name.",
                    nameof(quickActions));
            }

            var quickActionId = quickAction.Id.Trim();
            if (nextTokensByQuickActionId.ContainsKey(quickActionId))
            {
                throw new ArgumentException(
                    $"Quick-action identifier '{quickActionId}' occurs more than once.",
                    nameof(quickActions));
            }

            var token = GetOrCreateToken(quickActionId);
            items[index] = new(token, quickActionId, quickAction.DisplayName);
            quickActionIdsByToken.Add(token, quickActionId);
            nextTokensByQuickActionId.Add(quickActionId, token);
        }

        return new(items, quickActionIdsByToken, nextTokensByQuickActionId);
    }

    public void Commit(QuickActionTokenAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        tokensByQuickActionId = new Dictionary<string, ulong>(
            assignment.TokensByQuickActionId,
            StringComparer.Ordinal);
    }

    public static IReadOnlyDictionary<ulong, string> CreateTransitionMap(
        IReadOnlyDictionary<ulong, string> previousQuickActionIds,
        IReadOnlyDictionary<ulong, string> nextQuickActionIds)
    {
        foreach (var token in previousQuickActionIds.Keys)
        {
            if (nextQuickActionIds.ContainsKey(token))
            {
                continue;
            }

            var transition = new Dictionary<ulong, string>(
                previousQuickActionIds.Count + nextQuickActionIds.Count);
            foreach (var (previousToken, quickActionId) in previousQuickActionIds)
            {
                transition.Add(previousToken, quickActionId);
            }

            foreach (var (nextToken, quickActionId) in nextQuickActionIds)
            {
                transition[nextToken] = quickActionId;
            }

            return transition;
        }

        return nextQuickActionIds;
    }

    private ulong GetOrCreateToken(string quickActionId)
    {
        if (tokensByQuickActionId.TryGetValue(quickActionId, out var token))
        {
            return token;
        }

        if (nextToken == 0)
        {
            throw new InvalidOperationException("The quick-action token space has been exhausted.");
        }

        token = nextToken++;
        return token;
    }
}

internal sealed record QuickActionTokenAssignment(
    IReadOnlyList<QuickActionTokenItem> Items,
    IReadOnlyDictionary<ulong, string> QuickActionIdsByToken,
    IReadOnlyDictionary<string, ulong> TokensByQuickActionId);

internal sealed record QuickActionTokenItem(
    ulong Token,
    string QuickActionId,
    string DisplayName);

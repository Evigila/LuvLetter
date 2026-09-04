namespace LuvLetter.Core.Application;

public readonly struct ApplicationNameQuery
{
    internal ApplicationNameQuery(string? text, string? compactText)
    {
        Text = text;
        CompactText = compactText;
    }

    internal string? Text { get; }
    internal string? CompactText { get; }
    public bool IsEligible => Text is not null;
}

public readonly struct ApplicationNameIndex
{
    internal ApplicationNameIndex(string compactDisplayName, string[]? compactAliases)
    {
        CompactDisplayName = compactDisplayName;
        CompactAliases = compactAliases;
    }

    internal string? CompactDisplayName { get; }
    internal string[]? CompactAliases { get; }
}

public static class ApplicationNameMatcher
{
    // Null means ineligible. Compaction is an application-only alias strategy.
    public static int? Score(ApplicationEntry entry, string query)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var preparedQuery = CreateQuery(query);
        if (!preparedQuery.IsEligible) return null;
        var index = CreateIndex(entry);
        return Score(entry, index, preparedQuery);
    }

    public static ApplicationNameQuery CreateQuery(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        query = query.Trim();
        return query.Length == 0 || query.IndexOfAny(['\\', '/']) >= 0
            ? default
            : new(query, Compact(query));
    }

    public static ApplicationNameIndex CreateIndex(ApplicationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var compactDisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.DisplayName ?? string.Empty
            : Compact(entry.DisplayName);
        string[]? compactAliases = null;
        for (var index = 0; index < entry.Aliases.Length; index++)
        {
            var alias = entry.Aliases[index];
            if (string.IsNullOrWhiteSpace(alias)) continue;
            var compactAlias = alias.Equals(entry.DisplayName, StringComparison.Ordinal)
                ? compactDisplayName : Compact(alias);
            if (alias.Equals(compactAlias, StringComparison.Ordinal)) continue;
            compactAliases ??= (string[])entry.Aliases.Clone();
            compactAliases[index] = compactAlias;
        }
        return new(compactDisplayName, compactAliases);
    }

    public static int? Score(
        ApplicationEntry entry,
        ApplicationNameIndex index,
        ApplicationNameQuery query)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!query.IsEligible) return null;
        var compactDisplayName = index.CompactDisplayName;
        if (compactDisplayName is null
            || index.CompactAliases is { } compactAliases && compactAliases.Length != entry.Aliases.Length)
        {
            index = CreateIndex(entry);
            compactDisplayName = index.CompactDisplayName!;
        }

        var queryText = query.Text!;
        var compactQuery = query.CompactText!;
        int? best = ScoreName(entry.DisplayName, compactDisplayName, queryText, compactQuery, true);
        for (var aliasIndex = 0; aliasIndex < entry.Aliases.Length; aliasIndex++)
        {
            var alias = entry.Aliases[aliasIndex];
            var compactAlias = index.CompactAliases is null ? alias : index.CompactAliases[aliasIndex];
            var score = ScoreName(alias, compactAlias, queryText, compactQuery, false);
            if (score.HasValue && (!best.HasValue || score.Value > best.Value)) best = score;
        }
        return best;
    }

    private static int? ScoreName(
        string name,
        string compactName,
        string query,
        string compactQuery,
        bool displayName)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return displayName ? 300 : 280;
        if (compactQuery.Length != 0 && compactName.Equals(compactQuery, StringComparison.OrdinalIgnoreCase)) return 250;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 100;
        return compactQuery.Length != 0 && compactName.StartsWith(compactQuery, StringComparison.OrdinalIgnoreCase)
            ? 80 : null;
    }

    private static string Compact(string value)
    {
        var whitespaceCount = 0;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character)) whitespaceCount++;
        }
        if (whitespaceCount == 0) return value;
        return string.Create(value.Length - whitespaceCount, value, static (destination, source) =>
        {
            var destinationIndex = 0;
            foreach (var character in source)
                if (!char.IsWhiteSpace(character)) destination[destinationIndex++] = character;
        });
    }
}

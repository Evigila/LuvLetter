using System.Text;

namespace LuvLetter.Core.Application;

public static class ApplicationNameMatcher
{
    // Null means ineligible. Compaction is an application-only alias strategy.
    public static int? Score(ApplicationEntry entry, string query)
    {
        query = query.Trim();
        if (query.Length == 0 || query.IndexOfAny(['\\', '/']) >= 0) return null;
        var compactQuery = Compact(query);
        int? best = ScoreName(entry.DisplayName, query, compactQuery, true);
        foreach (var alias in entry.Aliases)
        {
            var score = ScoreName(alias, query, compactQuery, false);
            if (score.HasValue && (!best.HasValue || score.Value > best.Value)) best = score;
        }
        return best;
    }

    private static int? ScoreName(string name, string query, string compactQuery, bool displayName)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return displayName ? 300 : 280;
        var compactName = Compact(name);
        if (compactQuery.Length != 0 && compactName.Equals(compactQuery, StringComparison.OrdinalIgnoreCase)) return 250;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 100;
        return compactQuery.Length != 0 && compactName.StartsWith(compactQuery, StringComparison.OrdinalIgnoreCase)
            ? 80 : null;
    }

    private static string Compact(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!char.IsWhiteSpace(character)) result.Append(character);
        }
        return result.ToString();
    }
}

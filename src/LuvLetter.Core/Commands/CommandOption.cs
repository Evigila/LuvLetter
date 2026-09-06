namespace ArkheideSystem;

/// <summary>
/// Describes one optional command argument and all accepted spellings for it.
/// </summary>
public sealed class CommandOption
{
    public CommandOption(
        IEnumerable<string> names,
        string description,
        bool repeatable = false)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var normalizedNames = names
            .Select(static name => NormalizeName(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedNames.Length == 0)
        {
            throw new ArgumentException(
                "A command option must have at least one name.",
                nameof(names));
        }

        Names = Array.AsReadOnly(normalizedNames);
        Description = description.Trim();
        Repeatable = repeatable;
    }

    public IReadOnlyList<string> Names { get; }

    public string Description { get; }

    public bool Repeatable { get; }

    private static string NormalizeName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Any(char.IsWhiteSpace) || normalized.Contains('/'))
        {
            throw new ArgumentException(
                "A command option name must be one segment without '/'.",
                nameof(value));
        }
        return normalized;
    }
}

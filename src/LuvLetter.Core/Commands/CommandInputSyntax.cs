namespace ArkheideSystem;

internal static class CommandInputSyntax
{
    internal static string RemoveModePrefix(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var normalized = input.AsSpan().Trim();
        if (!normalized.IsEmpty && normalized[0] == '/')
        {
            normalized = normalized[1..].TrimStart();
        }

        return normalized.ToString();
    }

    internal static string RemoveModePrefixForCompletion(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var normalized = input.AsSpan().TrimStart();
        if (!normalized.IsEmpty && normalized[0] == '/')
        {
            normalized = normalized[1..].TrimStart();
        }

        return normalized.ToString();
    }
}

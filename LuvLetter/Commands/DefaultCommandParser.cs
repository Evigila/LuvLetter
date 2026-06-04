namespace LuvLetter.Commands;

public sealed class DefaultCommandParser : ICommandParser
{
    public ParsedCommand Parse(string input)
    {
        var normalized = input.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return ParsedCommand.Empty;
        }

        var separatorIndex = normalized.IndexOf(' ');
        if (separatorIndex < 0)
        {
            return new ParsedCommand(normalized, normalized, string.Empty);
        }

        var name = normalized[..separatorIndex];
        var arguments = normalized[(separatorIndex + 1)..].Trim();
        return new ParsedCommand(normalized, name, arguments);
    }
}

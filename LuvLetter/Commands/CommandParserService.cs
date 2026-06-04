namespace LuvLetter.Commands;

public sealed class CommandParserService : ICommandParserService
{
    public CommandRequest Parse(string input)
    {
        var normalized = input.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return CommandRequest.Empty;
        }

        var separatorIndex = normalized.IndexOf(' ');
        if (separatorIndex < 0)
        {
            return new CommandRequest(normalized, normalized, string.Empty);
        }

        var name = normalized[..separatorIndex];
        var arguments = normalized[(separatorIndex + 1)..].Trim();
        return new CommandRequest(normalized, name, arguments);
    }
}

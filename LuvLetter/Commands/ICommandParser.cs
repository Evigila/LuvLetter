namespace LuvLetter.Commands;

public interface ICommandParser
{
    ParsedCommand Parse(string input);
}

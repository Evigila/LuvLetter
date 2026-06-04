namespace LuvLetter.Commands;

public interface ICommandParserService
{
    CommandRequest Parse(string input);
}

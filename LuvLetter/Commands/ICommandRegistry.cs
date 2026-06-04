namespace LuvLetter.Commands;

public interface ICommandRegistry
{
    IReadOnlyCollection<CommandData> Commands { get; }

    bool TryResolve(CommandRequest request, out CommandData commandData);
}

namespace LuvLetter.Commands;

public interface ICommandHandler
{
    Task<CommandExecutionResult> HandleAsync(ParsedCommand command, CancellationToken cancellationToken = default);
}

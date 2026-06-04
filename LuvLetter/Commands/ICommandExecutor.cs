namespace LuvLetter.Commands;

public interface ICommandExecutor
{
    Task<CommandExecutionResult> ExecuteAsync(
        CommandRequest request,
        CancellationToken cancellationToken = default);
}

namespace LuvLetter.Commands;

public interface ICommandResponseBehavior
{
    Task<CommandExecutionResult> ExecuteAsync(
        CommandExecutionContext context,
        CancellationToken cancellationToken = default);
}

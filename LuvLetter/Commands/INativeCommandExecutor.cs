namespace LuvLetter.Commands;

public interface INativeCommandExecutor
{
    Task<CommandExecutionResult> ExecuteAsync(
        CommandExecutionContext context,
        CancellationToken cancellationToken = default);
}

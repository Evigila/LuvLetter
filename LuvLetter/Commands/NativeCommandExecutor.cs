namespace LuvLetter.Commands;

public sealed class NativeCommandExecutor : INativeCommandExecutor
{
    public Task<CommandExecutionResult> ExecuteAsync(
        CommandExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(
            new CommandExecutionResult(
                $"Native command routing is not implemented yet: {context.Command.Metadata.Name}"));
    }
}

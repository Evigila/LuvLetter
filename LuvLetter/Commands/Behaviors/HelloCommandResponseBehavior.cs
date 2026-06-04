namespace LuvLetter.Commands.Behaviors;

public sealed class HelloCommandResponseBehavior : ICommandResponseBehavior
{
    public Task<CommandExecutionResult> ExecuteAsync(
        CommandExecutionContext context,
        CancellationToken cancellationToken = default
    )
    {
        return Task.FromResult(new CommandExecutionResult("Hello World"));
    }
}

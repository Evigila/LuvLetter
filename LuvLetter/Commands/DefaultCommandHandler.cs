namespace LuvLetter.Commands;

public sealed class DefaultCommandHandler : ICommandHandler
{
    public Task<CommandExecutionResult> HandleAsync(ParsedCommand command, CancellationToken cancellationToken = default)
    {
        if (command.IsEmpty)
        {
            return Task.FromResult(CommandExecutionResult.Empty);
        }

        if (string.Equals(command.Name, "hello", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new CommandExecutionResult("Hello World"));
        }

        return Task.FromResult(new CommandExecutionResult($"Unknown command: {command.Name}"));
    }
}

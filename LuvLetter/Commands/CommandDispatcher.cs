namespace LuvLetter.Commands;

public sealed class CommandDispatcher
{
    private readonly ICommandParser parser;
    private readonly ICommandHandler handler;

    public CommandDispatcher(ICommandParser parser, ICommandHandler handler)
    {
        this.parser = parser;
        this.handler = handler;
    }

    public Task<CommandExecutionResult> DispatchAsync(string input, CancellationToken cancellationToken = default)
    {
        var command = parser.Parse(input);
        if (command.IsEmpty)
        {
            return Task.FromResult(CommandExecutionResult.Empty);
        }

        return handler.HandleAsync(command, cancellationToken);
    }
}

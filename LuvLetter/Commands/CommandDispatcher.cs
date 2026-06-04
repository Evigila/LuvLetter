namespace LuvLetter.Commands;

public sealed class CommandDispatcher(
    ICommandParserService parserService,
    ICommandExecutor commandExecutor
)
{
    private readonly ICommandParserService parserService = parserService;
    private readonly ICommandExecutor commandExecutor = commandExecutor;

    public Task<CommandExecutionResult> DispatchAsync(
        string input,
        CancellationToken cancellationToken = default
    )
    {
        var commandRequest = parserService.Parse(input);
        if (commandRequest.IsEmpty)
        {
            return Task.FromResult(CommandExecutionResult.Empty);
        }

        return commandExecutor.ExecuteAsync(commandRequest, cancellationToken);
    }
}

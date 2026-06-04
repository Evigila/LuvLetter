using Microsoft.Extensions.DependencyInjection;

namespace LuvLetter.Commands;

public sealed class CommandExecutor(
    ICommandRegistry commandRegistry,
    ICommandRouteService commandRouteService,
    INativeCommandExecutor nativeCommandExecutor,
    IServiceProvider serviceProvider
) : ICommandExecutor
{
    private readonly ICommandRegistry commandRegistry = commandRegistry;
    private readonly ICommandRouteService commandRouteService = commandRouteService;
    private readonly INativeCommandExecutor nativeCommandExecutor = nativeCommandExecutor;
    private readonly IServiceProvider serviceProvider = serviceProvider;

    public Task<CommandExecutionResult> ExecuteAsync(
        CommandRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (!commandRegistry.TryResolve(request, out var commandData))
        {
            return Task.FromResult(new CommandExecutionResult($"Unknown command: {request.Name}"));
        }

        var executionContext = new CommandExecutionContext(commandData, request);
        var executionTarget = commandRouteService.Resolve(executionContext);
        if (executionTarget == CommandExecutionTarget.Native)
        {
            return nativeCommandExecutor.ExecuteAsync(executionContext, cancellationToken);
        }

        var responseBehavior = CreateResponseBehavior(commandData);
        return responseBehavior.ExecuteAsync(executionContext, cancellationToken);
    }

    private ICommandResponseBehavior CreateResponseBehavior(CommandData commandData)
    {
        var instance = ActivatorUtilities.CreateInstance(
            serviceProvider,
            commandData.ResponseBehaviorType
        );
        if (instance is not ICommandResponseBehavior responseBehavior)
        {
            throw new InvalidOperationException(
                $"Response behavior must implement {nameof(ICommandResponseBehavior)}: {commandData.ResponseBehaviorType.FullName}"
            );
        }

        return responseBehavior;
    }
}

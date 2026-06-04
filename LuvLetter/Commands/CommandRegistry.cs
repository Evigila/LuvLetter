using LuvLetter.Commands.Data;

namespace LuvLetter.Commands;

public sealed class CommandRegistry : ICommandRegistry
{
    private readonly Dictionary<string, CommandData> commandMap = new(
        StringComparer.OrdinalIgnoreCase
    );

    public CommandRegistry()
    {
        var commands = CreateCommands();
        Commands = commands;

        foreach (var command in commands)
        {
            Register(command);
        }
    }

    public IReadOnlyCollection<CommandData> Commands { get; }

    public bool TryResolve(CommandRequest request, out CommandData commandData)
    {
        return commandMap.TryGetValue(request.Name, out commandData!);
    }

    private static IReadOnlyCollection<CommandData> CreateCommands()
    {
        return [new HelloCommandData()];
    }

    private void Register(CommandData command)
    {
        foreach (var routeKey in command.Metadata.GetRouteKeys())
        {
            if (!commandMap.TryAdd(routeKey, command))
            {
                throw new InvalidOperationException(
                    $"Duplicate command route key detected: {routeKey}"
                );
            }
        }
    }
}

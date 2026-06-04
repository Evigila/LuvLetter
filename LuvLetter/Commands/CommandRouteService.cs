namespace LuvLetter.Commands;

public sealed class CommandRouteService : ICommandRouteService
{
    public CommandExecutionTarget Resolve(CommandExecutionContext context)
    {
        return context.Command.Metadata.DefaultTarget;
    }
}

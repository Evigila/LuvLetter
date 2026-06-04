namespace LuvLetter.Commands;

public interface ICommandRouteService
{
    CommandExecutionTarget Resolve(CommandExecutionContext context);
}

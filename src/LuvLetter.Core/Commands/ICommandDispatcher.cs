namespace LuvLetter.Core.Commands;

public interface ICommandDispatcher : IDisposable
{
    event EventHandler<CommandInvocationEventArgs>? Unhandled;

    event EventHandler<CommandDispatchFailedEventArgs>? Failed;

    bool Register(
        string commandName,
        Action<CommandInvocation> handler,
        CommandRegistrationMode mode = CommandRegistrationMode.RejectDuplicate);

    bool Unregister(string commandName);

    CommandDispatchResult Dispatch(string commandText);
}

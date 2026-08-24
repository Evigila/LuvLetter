namespace LuvLetter.Core.Commands;

public interface ICommandDispatcher : ICommandRegistrar, IDisposable
{
    event EventHandler<CommandInvocationEventArgs>? Unhandled;

    event EventHandler<CommandDispatchFailedEventArgs>? Failed;

    bool Unregister(string commandName);

    CommandDispatchResult Dispatch(string commandText);
}

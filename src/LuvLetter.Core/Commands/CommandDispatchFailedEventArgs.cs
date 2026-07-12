namespace LuvLetter.Core.Commands;

public sealed class CommandDispatchFailedEventArgs(
    CommandInvocation invocation,
    Exception exception) : EventArgs
{
    public CommandInvocation Invocation { get; } = invocation;

    public Exception Exception { get; } = exception;
}

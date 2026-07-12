namespace LuvLetter.Core.Commands;

public sealed class CommandInvocationEventArgs(CommandInvocation invocation) : EventArgs
{
    public CommandInvocation Invocation { get; } = invocation;
}

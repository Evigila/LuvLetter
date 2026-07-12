namespace LuvLetter.Core.Commands;

public enum CommandDispatchResult
{
    Accepted = 0,
    RejectedEmpty = 1,
    QueueFull = 2,
    Disposed = 3,
}

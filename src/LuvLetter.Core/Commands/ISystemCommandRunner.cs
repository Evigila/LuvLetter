namespace ArkheideSystem;

public enum SystemCommandQueueStatus
{
    Accepted,
    RejectedEmpty,
    QueueFull,
    Stopping,
}

public readonly record struct SystemCommandEnqueueResult(
    SystemCommandQueueStatus Status,
    long RequestId);

public sealed record SystemCommandStarted(
    long RequestId,
    string CommandText);

public sealed record SystemCommandCompleted(
    long RequestId,
    string CommandText,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated,
    bool TimedOut,
    bool Cancelled,
    TimeSpan Duration);

public interface ISystemCommandRunner
{
    event Action<SystemCommandStarted>? Started;

    event Action<SystemCommandCompleted>? Completed;

    SystemCommandEnqueueResult TryEnqueue(string commandText);
}

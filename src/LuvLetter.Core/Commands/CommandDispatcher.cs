using LuvLetter.Core.Concurrency;

namespace LuvLetter.Core.Commands;

public enum CommandRegistrationMode
{
    RejectDuplicate,
    ReplaceExisting,
}

public enum CommandDispatchResult
{
    Accepted,
    RejectedEmpty,
    QueueFull,
    Disposed,
}

public sealed record CommandInvocation(
    string Text,
    string CommandName,
    string Arguments);

/// <summary>
/// A bounded, single-consumer command dispatcher. Dispatch never invokes user code
/// inline; one ThreadPool work item drains each burst of submitted commands.
/// </summary>
public sealed class CommandDispatcher : ICommandRegistrar, IDisposable
{
    private const int DefaultCapacity = 64;

    private readonly object handlersLock = new();
    private readonly Dictionary<string, Action<CommandInvocation>> handlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly BoundedSerialQueue<string> dispatchQueue;

    public CommandDispatcher(int capacity = DefaultCapacity)
    {
        dispatchQueue = new(capacity, Process);
    }

    public event Action<CommandInvocation>? Unhandled;

    public event Action<CommandInvocation, Exception>? Failed;

    public bool Register(
        string commandName,
        Action<CommandInvocation> handler,
        CommandRegistrationMode mode = CommandRegistrationMode.RejectDuplicate)
    {
        var normalizedName = NormalizeCommandName(commandName);
        ArgumentNullException.ThrowIfNull(handler);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        lock (handlersLock)
        {
            if (handlers.ContainsKey(normalizedName)
                && mode == CommandRegistrationMode.RejectDuplicate)
            {
                return false;
            }

            handlers[normalizedName] = handler;
            return true;
        }
    }

    public bool Unregister(string commandName)
    {
        var normalizedName = NormalizeCommandName(commandName);
        lock (handlersLock)
        {
            return handlers.Remove(normalizedName);
        }
    }

    public bool IsRegistered(string commandName)
    {
        var normalizedName = NormalizeCommandName(commandName);
        lock (handlersLock)
        {
            return handlers.ContainsKey(normalizedName);
        }
    }

    public bool IsRegisteredInvocation(string commandText)
    {
        ArgumentNullException.ThrowIfNull(commandText);
        var trimmedText = commandText.AsSpan().Trim();
        if (trimmedText.Length == 0)
        {
            return false;
        }

        var separator = IndexOfWhitespace(trimmedText);
        var commandName = separator < 0 ? trimmedText : trimmedText[..separator];
        lock (handlersLock)
        {
            return handlers.ContainsKey(commandName.ToString());
        }
    }

    /// <summary>
    /// Returns an immutable, deterministically ordered view of currently registered names.
    /// </summary>
    public IReadOnlyList<string> RegisteredNamesSnapshot()
    {
        lock (handlersLock)
        {
            return handlers.Keys
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static name => name, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public CommandDispatchResult Dispatch(string commandText)
    {
        ArgumentNullException.ThrowIfNull(commandText);
        if (dispatchQueue.IsDisposed)
        {
            return CommandDispatchResult.Disposed;
        }

        var trimmedText = commandText.AsSpan().Trim();
        if (trimmedText.Length == 0)
        {
            return CommandDispatchResult.RejectedEmpty;
        }

        // Own the submitted memory before returning to a native callback boundary.
        var ownedText = new string(trimmedText);

        if (!dispatchQueue.TryEnqueue(ownedText))
        {
            return dispatchQueue.IsDisposed
                ? CommandDispatchResult.Disposed
                : CommandDispatchResult.QueueFull;
        }

        return CommandDispatchResult.Accepted;
    }

    public void Dispose()
    {
        dispatchQueue.Dispose();

        lock (handlersLock)
        {
            handlers.Clear();
        }
    }

    private void Process(string commandText)
    {
        var separator = IndexOfWhitespace(commandText);
        var commandName = separator < 0 ? commandText : commandText[..separator];
        var arguments = separator < 0 ? string.Empty : commandText[(separator + 1)..].TrimStart();
        var invocation = new CommandInvocation(commandText, commandName, arguments);

        Action<CommandInvocation>? handler;
        lock (handlersLock)
        {
            handlers.TryGetValue(commandName, out handler);
        }

        if (handler is null)
        {
            RaiseSafely(Unhandled, invocation);
            return;
        }

        try
        {
            handler(invocation);
        }
        catch (Exception exception)
        {
            RaiseSafely(Failed, invocation, exception);
        }
    }

    private static string NormalizeCommandName(string commandName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        var normalized = commandName.Trim();
        if (IndexOfWhitespace(normalized) >= 0)
        {
            throw new ArgumentException(
                "A command name cannot contain whitespace.",
                nameof(commandName));
        }

        return normalized;
    }

    private static int IndexOfWhitespace(ReadOnlySpan<char> value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static void RaiseSafely<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch
            {
                // Notification consumers cannot terminate the dispatcher drain.
            }
        }
    }

    private static void RaiseSafely<T1, T2>(Action<T1, T2>? handlers, T1 first, T2 second)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<T1, T2> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(first, second);
            }
            catch
            {
                // Notification consumers cannot terminate the dispatcher drain.
            }
        }
    }
}

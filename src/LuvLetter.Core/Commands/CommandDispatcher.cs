using System.Collections.Concurrent;

namespace LuvLetter.Core.Commands;

/// <summary>
/// A bounded, single-consumer command dispatcher. Dispatch never invokes user code
/// inline; one ThreadPool work item drains each burst of submitted commands.
/// </summary>
public sealed class CommandDispatcher : ICommandDispatcher
{
    private const int DefaultCapacity = 64;

    private readonly object handlersLock = new();
    private readonly Dictionary<string, Action<CommandInvocation>> handlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> pending = new();
    private readonly int capacity;
    private int pendingCount;
    private int drainScheduled;
    private int disposed;

    public CommandDispatcher(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.capacity = capacity;
    }

    public event EventHandler<CommandInvocationEventArgs>? Unhandled;

    public event EventHandler<CommandDispatchFailedEventArgs>? Failed;

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

    public CommandDispatchResult Dispatch(string commandText)
    {
        ArgumentNullException.ThrowIfNull(commandText);
        if (Volatile.Read(ref disposed) != 0)
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

        if (!TryReserveQueueSlot())
        {
            return CommandDispatchResult.QueueFull;
        }

        pending.Enqueue(ownedText);
        ScheduleDrain();
        return CommandDispatchResult.Accepted;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        while (pending.TryDequeue(out _))
        {
            Interlocked.Decrement(ref pendingCount);
        }

        lock (handlersLock)
        {
            handlers.Clear();
        }
    }

    private bool TryReserveQueueSlot()
    {
        while (true)
        {
            var count = Volatile.Read(ref pendingCount);
            if (count >= capacity)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref pendingCount, count + 1, count) == count)
            {
                return true;
            }
        }
    }

    private void ScheduleDrain()
    {
        if (Interlocked.CompareExchange(ref drainScheduled, 1, 0) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static (CommandDispatcher dispatcher) => dispatcher.Drain(),
                this,
                preferLocal: false);
        }
    }

    private void Drain()
    {
        while (true)
        {
            while (pending.TryDequeue(out var commandText))
            {
                Interlocked.Decrement(ref pendingCount);
                if (Volatile.Read(ref disposed) == 0)
                {
                    Process(commandText);
                }
            }

            Volatile.Write(ref drainScheduled, 0);
            if (pending.IsEmpty
                || Interlocked.CompareExchange(ref drainScheduled, 1, 0) != 0)
            {
                return;
            }
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
            RaiseSafely(Unhandled, new CommandInvocationEventArgs(invocation));
            return;
        }

        try
        {
            handler(invocation);
        }
        catch (Exception exception)
        {
            RaiseSafely(Failed, new CommandDispatchFailedEventArgs(invocation, exception));
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

    private void RaiseSafely<TEventArgs>(EventHandler<TEventArgs>? handlers, TEventArgs args)
        where TEventArgs : EventArgs
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Notification consumers cannot terminate the dispatcher drain.
            }
        }
    }
}

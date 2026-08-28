namespace LuvLetter.Core.NativeShell;

/// <summary>
/// A non-blocking, single-consumer dispatcher with one replaceable pending value.
/// </summary>
internal sealed class LatestCallbackDispatcher<T> : IDisposable
{
    private readonly object stateLock = new();
    private readonly Action<T> consumer;
    private T? pending;
    private bool hasPending;
    private bool drainScheduled;
    private bool disposed;

    public LatestCallbackDispatcher(Action<T> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        this.consumer = consumer;
    }

    public bool TryPublish(T value)
    {
        lock (stateLock)
        {
            if (disposed)
            {
                return false;
            }

            pending = value;
            hasPending = true;
            if (drainScheduled)
            {
                return true;
            }

            drainScheduled = true;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static (LatestCallbackDispatcher<T> dispatcher) => dispatcher.Drain(),
            this,
            preferLocal: false);
        return true;
    }

    public void Dispose()
    {
        lock (stateLock)
        {
            disposed = true;
            pending = default;
            hasPending = false;
        }
    }

    private void Drain()
    {
        while (true)
        {
            T value;
            lock (stateLock)
            {
                if (disposed || !hasPending)
                {
                    drainScheduled = false;
                    return;
                }

                value = pending!;
                pending = default;
                hasPending = false;
            }

            try
            {
                consumer(value);
            }
            catch
            {
                // A failed edit cannot terminate delivery of the next one.
            }
        }
    }
}

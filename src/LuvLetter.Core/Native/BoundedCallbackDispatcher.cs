using System.Collections.Concurrent;

namespace LuvLetter.Core.Native;

/// <summary>
/// Moves native callback work onto a bounded, serial ThreadPool drain. Producers never
/// block a native UI thread; overflow is observable through <see cref="DroppedCount"/>.
/// </summary>
internal sealed class BoundedCallbackDispatcher<T> : IDisposable
{
    private readonly ConcurrentQueue<T> pending = new();
    private readonly Action<T> consumer;
    private readonly int capacity;
    private int pendingCount;
    private int drainScheduled;
    private int disposed;
    private long droppedCount;

    public BoundedCallbackDispatcher(int capacity, Action<T> consumer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentNullException.ThrowIfNull(consumer);
        this.capacity = capacity;
        this.consumer = consumer;
    }

    public long DroppedCount => Interlocked.Read(ref droppedCount);

    public bool TryEnqueue(T item)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return false;
        }

        if (!TryReserveSlot())
        {
            Interlocked.Increment(ref droppedCount);
            return false;
        }

        pending.Enqueue(item);
        ScheduleDrain();
        return true;
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
    }

    private bool TryReserveSlot()
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
                static (BoundedCallbackDispatcher<T> dispatcher) => dispatcher.Drain(),
                this,
                preferLocal: false);
        }
    }

    private void Drain()
    {
        while (true)
        {
            while (pending.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref pendingCount);
                if (Volatile.Read(ref disposed) == 0)
                {
                    try
                    {
                        consumer(item);
                    }
                    catch
                    {
                        // Callback consumers are isolated from the shared drain.
                    }
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
}

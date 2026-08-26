using System.Collections.Concurrent;

namespace LuvLetter.Core.Concurrency;

/// <summary>
/// A bounded, single-consumer ThreadPool queue. Enqueue never waits for the
/// consumer, and one failed item cannot terminate the shared drain.
/// </summary>
internal sealed class BoundedSerialQueue<T> : IDisposable
{
    private readonly ConcurrentQueue<T> pending = new();
    private readonly Action<T> consumer;
    private readonly int capacity;
    private int pendingCount;
    private int drainScheduled;
    private int disposed;
    private long rejectedCount;

    public BoundedSerialQueue(int capacity, Action<T> consumer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentNullException.ThrowIfNull(consumer);
        this.capacity = capacity;
        this.consumer = consumer;
    }

    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>
    /// Counts capacity rejections. Enqueues rejected after disposal are not counted.
    /// </summary>
    public long RejectedCount => Interlocked.Read(ref rejectedCount);

    public bool TryEnqueue(T item)
    {
        if (IsDisposed)
        {
            return false;
        }

        if (!TryReserveSlot())
        {
            Interlocked.Increment(ref rejectedCount);
            return false;
        }

        // Dispose may have started while the slot was being reserved. Return the
        // reservation instead of publishing new work after that point.
        if (IsDisposed)
        {
            Interlocked.Decrement(ref pendingCount);
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
                static (BoundedSerialQueue<T> queue) => queue.Drain(),
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
                if (IsDisposed)
                {
                    continue;
                }

                try
                {
                    consumer(item);
                }
                catch
                {
                    // A consumer failure belongs to that item and must not terminate
                    // the serial drain or escape onto a ThreadPool thread.
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

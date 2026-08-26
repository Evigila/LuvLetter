using LuvLetter.Core.Concurrency;

namespace LuvLetter.Core.NativeShell;

/// <summary>
/// Moves native callback work onto a bounded, serial ThreadPool drain. Producers never
/// block a native UI thread; overflow is observable through <see cref="DroppedCount"/>.
/// </summary>
internal sealed class BoundedCallbackDispatcher<T> : IDisposable
{
    private readonly BoundedSerialQueue<T> queue;

    public BoundedCallbackDispatcher(int capacity, Action<T> consumer)
    {
        queue = new(capacity, consumer);
    }

    public long DroppedCount => queue.RejectedCount;

    public bool TryEnqueue(T item)
    {
        return queue.TryEnqueue(item);
    }

    public void Dispose() => queue.Dispose();
}

using System;
using System.Collections.Generic;

namespace Bezoro.GameSystems.Internal;

/// <summary>Serializes queue bookkeeping while retaining warmed storage and caller-owned payloads.</summary>
internal sealed class SynchronizedEventQueue<T>
{
    // This queue is private and never escapes, so its monitor needs no separate object.
    private readonly Queue<T> _items;
    internal int MaxCapacity { get; }

    internal SynchronizedEventQueue(int initialCapacity, int maxCapacity)
    {
        if (initialCapacity < 0) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        if (maxCapacity < 0) throw new ArgumentOutOfRangeException(nameof(maxCapacity));
        if (initialCapacity > maxCapacity) throw new ArgumentException("Initial capacity must not exceed maximum capacity.", nameof(initialCapacity));
        _items = new Queue<T>(initialCapacity);
        MaxCapacity = maxCapacity;
    }

    internal int Count { get { lock (_items) return _items.Count; } }

    internal void Enqueue(T item)
    {
        if (!TryEnqueue(item)) throw new InvalidOperationException("The event queue has reached its configured capacity.");
    }

    internal bool TryEnqueue(T item)
    {
        lock (_items)
        {
            if (_items.Count == MaxCapacity) return false;
            _items.Enqueue(item);
            return true;
        }
    }

    internal bool TryDequeue(out T item)
    {
        lock (_items) return _items.TryDequeue(out item!);
    }

    internal void Clear() { lock (_items) _items.Clear(); }
}

namespace Bezoro.Simulation.Timing;

/// <summary>
/// A fixed-capacity integer-tick scheduler with stable ordering and generational cancellation.
/// Operations require one owning thread or external serialization. Separate instances are independent.
/// </summary>
/// <remarks>
/// Scheduling, cancellation, rescheduling, and draining allocate no managed memory after construction.
/// The scheduler retains payload references until cancellation, draining, or Clear.
/// </remarks>
public sealed class TickScheduler<T>
{
    private struct Node
    {
        internal T Value;
        internal long DueTick;
        internal ulong Sequence;
        internal uint Generation;
        internal int HeapIndex;
        internal int NextFree;
        internal bool Active;
    }

    private readonly Node[] _nodes;
    private readonly int[] _heap;
    private readonly long _ownerId;
    private int _count;
    private int _freeHead;
    private int _remainingCapacity;
    private ulong _nextSequence;

    /// <summary>Preallocates a fixed number of slots. Zero capacity is valid and always full.</summary>
    public TickScheduler(int capacity, long initialTick = 0)
    {
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (initialTick < 0) throw new ArgumentOutOfRangeException(nameof(initialTick));
        _nodes = new Node[capacity];
        _heap = new int[capacity];
        _ownerId = SchedulerOwnerIds.Next();
        _remainingCapacity = capacity;
        _freeHead = capacity == 0 ? -1 : 0;
        CurrentTick = initialTick;
        for (int slot = 0; slot < capacity; slot++)
        {
            _nodes[slot].Generation = 1;
            _nodes[slot].HeapIndex = -1;
            _nodes[slot].NextFree = slot + 1 == capacity ? -1 : slot + 1;
        }
    }

    /// <summary>Gets the preallocated number of slots.</summary>
    public int Capacity => _nodes.Length;

    /// <summary>Gets the number of pending items.</summary>
    public int Count => _count;

    /// <summary>Gets available slots, excluding slots retired after generation exhaustion.</summary>
    public int RemainingCapacity => _remainingCapacity;

    /// <summary>Gets slots permanently retired to prevent stale-handle resurrection.</summary>
    public int RetiredSlotCount => Capacity - _count - _remainingCapacity;

    /// <summary>Gets the latest tick supplied to AdvanceTo. Clear retains this value.</summary>
    public long CurrentTick { get; private set; }

    /// <summary>Gets whether at least one item is due at CurrentTick.</summary>
    public bool HasDueItems => _count != 0 && _nodes[_heap[0]].DueTick <= CurrentTick;

    /// <summary>Schedules a payload after a nonnegative delay. Throws if capacity is exhausted.</summary>
    public ScheduledHandle Schedule(T value, long delayTicks)
    {
        if (!TrySchedule(value, delayTicks, out var handle))
            throw new InvalidOperationException("The scheduler has no available slots.");
        return handle;
    }

    /// <summary>Schedules a payload at or after CurrentTick. Throws if capacity is exhausted.</summary>
    public ScheduledHandle ScheduleAt(T value, long dueTick)
    {
        if (!TryScheduleAt(value, dueTick, out var handle))
            throw new InvalidOperationException("The scheduler has no available slots.");
        return handle;
    }

    /// <summary>Attempts to schedule a payload after a nonnegative delay. Invalid arguments still throw.</summary>
    public bool TrySchedule(T value, long delayTicks, out ScheduledHandle handle)
    {
        if (delayTicks < 0) throw new ArgumentOutOfRangeException(nameof(delayTicks));
        return TryScheduleAt(value, checked(CurrentTick + delayTicks), out handle);
    }

    /// <summary>
    /// Attempts to schedule a payload at an absolute tick. Equal due ticks retain scheduling order.
    /// Invalid time throws without changing the scheduler. If capacity is available,
    /// exhausted insertion-order identities also throw without changing the scheduler.
    /// </summary>
    public bool TryScheduleAt(T value, long dueTick, out ScheduledHandle handle)
    {
        if (dueTick < CurrentTick) throw new ArgumentOutOfRangeException(nameof(dueTick));
        handle = default;
        if (_freeHead < 0) return false;
        if (_nextSequence == ulong.MaxValue)
            throw new OverflowException("Insertion-order identities are exhausted. Clear the scheduler before reuse.");

        int slot = _freeHead;
        ref var node = ref _nodes[slot];
        _freeHead = node.NextFree;
        _remainingCapacity--;
        node.Value = value;
        node.DueTick = dueTick;
        node.Sequence = _nextSequence++;
        node.HeapIndex = _count;
        node.NextFree = -1;
        node.Active = true;
        _heap[_count++] = slot;
        SiftUp(node.HeapIndex);
        handle = new(_ownerId, slot, node.Generation);
        return true;
    }

    /// <summary>Gets whether a handle currently identifies an item in this scheduler.</summary>
    public bool Contains(ScheduledHandle handle) => IsActive(handle);

    /// <summary>Reads a scheduled item without removing it. Foreign, default, and stale handles return false.</summary>
    public bool TryGet(ScheduledHandle handle, out ScheduledItem<T> item)
    {
        if (!IsActive(handle))
        {
            item = default;
            return false;
        }
        item = ItemAt(handle.Slot);
        return true;
    }

    /// <summary>Cancels an item and returns its payload. Invalid handles leave the scheduler unchanged.</summary>
    public bool TryCancel(ScheduledHandle handle, out T value)
    {
        if (!IsActive(handle))
        {
            value = default!;
            return false;
        }
        value = _nodes[handle.Slot].Value;
        RemoveAt(_nodes[handle.Slot].HeapIndex);
        return true;
    }

    /// <summary>
    /// Changes an item's absolute due tick, retaining its original tie-breaking insertion order.
    /// Invalid times throw even for a stale handle; invalid handles otherwise return false.
    /// </summary>
    public bool TryRescheduleAt(ScheduledHandle handle, long dueTick)
    {
        if (dueTick < CurrentTick) throw new ArgumentOutOfRangeException(nameof(dueTick));
        if (!IsActive(handle)) return false;
        ref var node = ref _nodes[handle.Slot];
        node.DueTick = dueTick;
        RepairHeap(node.HeapIndex);
        return true;
    }

    /// <summary>Reads the earliest scheduled item, whether or not it is due.</summary>
    public bool TryPeek(out ScheduledItem<T> item)
    {
        if (_count == 0)
        {
            item = default;
            return false;
        }
        item = ItemAt(_heap[0]);
        return true;
    }

    /// <summary>
    /// Advances monotonically to an absolute tick and drains into the supplied buffer.
    /// A full or empty buffer leaves remaining due items queued for DrainDue.
    /// </summary>
    public SchedulerDrainResult AdvanceTo(long tick, Span<ScheduledItem<T>> destination)
    {
        if (tick < CurrentTick) throw new ArgumentOutOfRangeException(nameof(tick));
        CurrentTick = tick;
        return DrainDue(destination);
    }

    /// <summary>Drains a bounded prefix of due work in due-tick and insertion order.</summary>
    public SchedulerDrainResult DrainDue(Span<ScheduledItem<T>> destination)
    {
        int written = 0;
        while (written < destination.Length && HasDueItems)
        {
            destination[written++] = ItemAt(_heap[0]);
            RemoveAt(0);
        }
        return new(written, HasDueItems);
    }

    /// <summary>
    /// Removes all items, clears payload references, and invalidates their handles in O(Count).
    /// CurrentTick and retired slots are retained; insertion-order numbering restarts.
    /// </summary>
    public void Clear()
    {
        while (_count > 0)
        {
            int heapIndex = --_count;
            ReleaseSlot(_heap[heapIndex]);
            _heap[heapIndex] = 0;
        }
        _nextSequence = 0;
    }

    private bool IsActive(ScheduledHandle handle) =>
        handle.OwnerId == _ownerId && unchecked((uint)handle.Slot) < (uint)_nodes.Length &&
        _nodes[handle.Slot].Active && _nodes[handle.Slot].Generation == handle.Generation;

    private ScheduledItem<T> ItemAt(int slot)
    {
        ref var node = ref _nodes[slot];
        return new(new(_ownerId, slot, node.Generation), node.DueTick, node.Value);
    }

    private void RemoveAt(int heapIndex)
    {
        int removedSlot = _heap[heapIndex];
        int lastIndex = --_count;
        if (heapIndex != lastIndex)
        {
            int movedSlot = _heap[lastIndex];
            _heap[heapIndex] = movedSlot;
            _nodes[movedSlot].HeapIndex = heapIndex;
            RepairHeap(heapIndex);
        }
        _heap[lastIndex] = 0;
        ReleaseSlot(removedSlot);
    }

    private void ReleaseSlot(int slot)
    {
        ref var node = ref _nodes[slot];
        node.Value = default!;
        node.Active = false;
        node.HeapIndex = -1;
        if (node.Generation == uint.MaxValue)
        {
            node.NextFree = -1;
            return;
        }
        node.Generation++;
        node.NextFree = _freeHead;
        _freeHead = slot;
        _remainingCapacity++;
    }

    private bool Earlier(int leftSlot, int rightSlot)
    {
        ref var left = ref _nodes[leftSlot];
        ref var right = ref _nodes[rightSlot];
        return left.DueTick < right.DueTick ||
               (left.DueTick == right.DueTick && left.Sequence < right.Sequence);
    }

    private void RepairHeap(int index)
    {
        if (index > 0 && Earlier(_heap[index], _heap[(index - 1) / 2]))
            SiftUp(index);
        else
            SiftDown(index);
    }

    private void SiftUp(int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (!Earlier(_heap[index], _heap[parent])) break;
            Swap(index, parent);
            index = parent;
        }
    }

    private void SiftDown(int index)
    {
        while (index < _count / 2)
        {
            int left = index * 2 + 1;
            int right = left + 1;
            int earliest = right < _count && Earlier(_heap[right], _heap[left]) ? right : left;
            if (!Earlier(_heap[earliest], _heap[index])) break;
            Swap(index, earliest);
            index = earliest;
        }
    }

    private void Swap(int left, int right)
    {
        int leftSlot = _heap[left];
        int rightSlot = _heap[right];
        _heap[left] = rightSlot;
        _heap[right] = leftSlot;
        _nodes[rightSlot].HeapIndex = left;
        _nodes[leftSlot].HeapIndex = right;
    }
}

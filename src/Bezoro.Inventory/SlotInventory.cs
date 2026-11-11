using System.Threading;

namespace Bezoro.Inventory;

/// <summary>Stores item stacks in a fixed number of slots.</summary>
/// <remarks>
/// Successful mutations retain catalog limits. Capacity failures return false without modifying any slot.
/// Public operations are thread-safe. Transfers lock both inventories in a consistent order; snapshots never expose mutable storage.
/// Normal data operations invoke no application callbacks and allocate no managed storage after construction; exceptions allocate as usual.
/// </remarks>
public sealed class SlotInventory
{
    private static long _nextLockOrder;
    private readonly object _sync = new();
    private readonly long _lockOrder = Interlocked.Increment(ref _nextLockOrder);
    private readonly ItemStack[] _slots;
    private int _occupiedSlotCount;

    /// <summary>Creates an empty inventory with fixed capacity. Zero slots are allowed.</summary>
    public SlotInventory(ItemCatalog catalog, int capacity)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Catalog = catalog;
        _slots = capacity == 0 ? Array.Empty<ItemStack>() : new ItemStack[capacity];
    }

    /// <summary>Gets the immutable catalog shared by compatible inventories.</summary>
    public ItemCatalog Catalog { get; }
    /// <summary>Gets the fixed number of slots.</summary>
    public int Capacity => _slots.Length;
    /// <summary>Gets the number of nonempty slots.</summary>
    public int OccupiedSlotCount { get { lock (_sync) return _occupiedSlotCount; } }
    /// <summary>Copies a coherent slot snapshot into caller-owned storage without allocating.</summary>
    public void CopyTo(Span<ItemStack> destination)
    {
        lock (_sync)
        {
            if (destination.Length < _slots.Length) throw new ArgumentException("The destination must fit every slot.", nameof(destination));
            _slots.AsSpan().CopyTo(destination);
        }
    }

    /// <summary>Allocates an independent, coherent snapshot of every slot.</summary>
    public ItemStack[] CreateSnapshot()
    {
        lock (_sync) return _slots.Length == 0 ? Array.Empty<ItemStack>() : (ItemStack[])_slots.Clone();
    }

    /// <summary>Gets a value snapshot of one slot.</summary>
    public ItemStack this[int index]
    {
        get
        {
            lock (_sync)
            {
                ValidateIndex(index);
                return _slots[index];
            }
        }
    }

    /// <summary>Counts one catalog item's total quantity across every slot.</summary>
    public long GetQuantity(ItemId item)
    {
        lock (_sync)
        {
            Catalog.GetDefinition(item);
            return CountUnchecked(item);
        }
    }

    /// <summary>Counts the quantity of an item that could fit in partial stacks and empty slots.</summary>
    public long GetAvailableCapacity(ItemId item)
    {
        lock (_sync)
        {
            var definition = Catalog.GetDefinition(item);
            return AvailableCapacityUnchecked(definition);
        }
    }

    /// <summary>Adds an aggregate positive quantity, filling matching partial stacks before empty slots in ascending index order.</summary>
    /// <returns>False when the complete quantity does not fit. No partial insertion occurs.</returns>
    public bool TryAdd(ItemId item, long quantity)
    {
        lock (_sync)
        {
            ValidateQuantity(quantity);
            var definition = Catalog.GetDefinition(item);
            if (AvailableCapacityUnchecked(definition) < quantity) return false;
            AddUnchecked(definition, quantity);
            return true;
        }
    }

    /// <summary>Adds a stack's quantity across available slots. An empty stack succeeds without changing storage.</summary>
    /// <remarks>The aggregate can split across slots even if the supplied stack exceeds the catalog's per-slot limit.</remarks>
    public bool TryAdd(ItemStack stack)
    {
        ValidateStack(stack);
        return stack.IsEmpty || TryAdd(stack.Item, stack.Quantity);
    }

    /// <summary>Removes a positive aggregate quantity from matching slots in ascending index order.</summary>
    /// <returns>False when the inventory does not hold the complete quantity. No partial removal occurs.</returns>
    public bool TryRemove(ItemId item, long quantity)
    {
        lock (_sync)
        {
            ValidateQuantity(quantity);
            Catalog.GetDefinition(item);
            if (CountUnchecked(item) < quantity) return false;
            RemoveUnchecked(item, quantity);
            return true;
        }
    }

    /// <summary>Inserts a complete stack into one empty or matching slot. An empty stack is a no-op.</summary>
    /// <returns>False when the slot contains another item or the complete quantity exceeds its stack limit.</returns>
    public bool TryInsertAt(int index, ItemStack stack)
    {
        lock (_sync)
        {
            ValidateIndex(index);
            ValidateStack(stack);
            if (stack.IsEmpty) return true;
            var definition = Catalog.GetDefinition(stack.Item);
            var current = _slots[index];
            if (!current.IsEmpty && current.Item != stack.Item) return false;
            if (stack.Quantity > definition.MaximumStackSize - current.Quantity) return false;
            if (current.IsEmpty) _occupiedSlotCount++;
            _slots[index] = new ItemStack(stack.Item, current.Quantity + stack.Quantity);
            return true;
        }
    }

    /// <summary>Takes a positive quantity from one slot, returning an empty output if the complete quantity is unavailable.</summary>
    public bool TryTakeAt(int index, int quantity, out ItemStack taken)
    {
        lock (_sync)
        {
            ValidateIndex(index);
            ValidateQuantity(quantity);
            var current = _slots[index];
            if (current.Quantity < quantity)
            {
                taken = default;
                return false;
            }
            taken = new ItemStack(current.Item, quantity);
            int remaining = current.Quantity - quantity;
            _slots[index] = remaining == 0 ? default : new ItemStack(current.Item, remaining);
            if (remaining == 0) _occupiedSlotCount--;
            return true;
        }
    }

    /// <summary>Transfers a positive aggregate quantity atomically between inventories sharing this catalog instance.</summary>
    /// <returns>False when the source quantity or complete destination capacity is insufficient.</returns>
    /// <remarks>
    /// Both inventories are locked for the complete transfer, in the same order for opposite-direction requests.
    /// A transfer to the same inventory succeeds without mutation only if the requested quantity exists.
    /// Different catalog instances throw even when their definitions happen to match.
    /// </remarks>
    public bool TryTransferTo(SlotInventory destination, ItemId item, long quantity)
    {
        if (destination is null) throw new ArgumentNullException(nameof(destination));
        ValidateQuantity(quantity);
        var definition = Catalog.GetDefinition(item);
        if (!ReferenceEquals(Catalog, destination.Catalog))
            throw new ArgumentException("Transfers require the same catalog instance.", nameof(destination));
        var first = _lockOrder < destination._lockOrder ? this : destination;
        var second = ReferenceEquals(first, this) ? destination : this;
        lock (first._sync)
        lock (second._sync)
        {
            if (CountUnchecked(item) < quantity) return false;
            if (ReferenceEquals(this, destination)) return true;
            if (destination.AvailableCapacityUnchecked(definition) < quantity) return false;
            RemoveUnchecked(item, quantity);
            destination.AddUnchecked(definition, quantity);
            return true;
        }
    }

    /// <summary>Replaces all slots from a complete snapshot after validating its length, item identifiers, and per-slot limits.</summary>
    /// <returns>False for a malformed snapshot, leaving the inventory unchanged.</returns>
    /// <remarks>The caller retains exclusive access to the input until this call returns. A snapshot carries no catalog identity; the application owns its schema.</remarks>
    public bool TryRestore(ReadOnlySpan<ItemStack> snapshot)
    {
        lock (_sync)
        {
            if (snapshot.Length != Capacity) return false;
            int occupied = 0;
            foreach (var stack in snapshot)
            {
                if (stack.IsEmpty)
                {
                    if (stack.Item.IsValid) return false;
                    continue;
                }
                if (stack.Quantity < 0 || !Catalog.TryGetDefinition(stack.Item, out var definition) ||
                    stack.Quantity > definition.MaximumStackSize)
                    return false;
                occupied++;
            }
            snapshot.CopyTo(_slots);
            _occupiedSlotCount = occupied;
            return true;
        }
    }

    /// <summary>Empties every slot while retaining storage.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            if (_occupiedSlotCount == 0) return;
            Array.Clear(_slots, 0, _slots.Length);
            _occupiedSlotCount = 0;
        }
    }

    private long CountUnchecked(ItemId item)
    {
        long quantity = 0;
        foreach (var stack in _slots)
            if (stack.Item == item) quantity += stack.Quantity;
        return quantity;
    }

    private long AvailableCapacityUnchecked(ItemDefinition definition)
    {
        long available = 0;
        foreach (var stack in _slots)
        {
            if (stack.IsEmpty) available += definition.MaximumStackSize;
            else if (stack.Item == definition.Item) available += definition.MaximumStackSize - stack.Quantity;
        }
        return available;
    }

    private void AddUnchecked(ItemDefinition definition, long quantity)
    {
        for (int index = 0; index < _slots.Length && quantity != 0; index++)
        {
            var stack = _slots[index];
            if (stack.Item != definition.Item) continue;
            int added = (int)Math.Min(quantity, definition.MaximumStackSize - stack.Quantity);
            if (added == 0) continue;
            _slots[index] = new ItemStack(definition.Item, stack.Quantity + added);
            quantity -= added;
        }
        for (int index = 0; index < _slots.Length && quantity != 0; index++)
        {
            if (!_slots[index].IsEmpty) continue;
            int added = (int)Math.Min(quantity, definition.MaximumStackSize);
            _slots[index] = new ItemStack(definition.Item, added);
            _occupiedSlotCount++;
            quantity -= added;
        }
    }

    private void RemoveUnchecked(ItemId item, long quantity)
    {
        for (int index = 0; index < _slots.Length && quantity != 0; index++)
        {
            var stack = _slots[index];
            if (stack.Item != item) continue;
            int removed = (int)Math.Min(quantity, stack.Quantity);
            int remaining = stack.Quantity - removed;
            _slots[index] = remaining == 0 ? default : new ItemStack(item, remaining);
            if (remaining == 0) _occupiedSlotCount--;
            quantity -= removed;
        }
    }

    private void ValidateIndex(int index)
    {
        if (unchecked((uint)index) >= (uint)_slots.Length) throw new ArgumentOutOfRangeException(nameof(index));
    }

    private static void ValidateStack(ItemStack stack)
    {
        if (stack.Quantity < 0 || (stack.IsEmpty && stack.Item.IsValid))
            throw new ArgumentException("A stack must have a positive quantity or be the canonical empty value.", nameof(stack));
    }

    private static void ValidateQuantity(long quantity)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "Quantities must be positive.");
    }
}

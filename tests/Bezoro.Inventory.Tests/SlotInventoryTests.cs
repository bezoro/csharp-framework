using System.Runtime.InteropServices;
using Xunit;

namespace Bezoro.Inventory.Tests;

public sealed class SlotInventoryTests
{
    private static readonly ItemId Wood = new(1);
    private static readonly ItemId Stone = new(2);
    private static readonly ItemId Tool = new(3);

    private static ItemCatalog Catalog() => new(new[]
    {
        new ItemDefinition(Wood, 10), new ItemDefinition(Stone, 4), new ItemDefinition(Tool, 1)
    });

    [Fact]
    public void Construction_HasFixedEmptyStorageAndRejectsInvalidCapacity()
    {
        Assert.Throws<ArgumentNullException>(() => new SlotInventory(null!, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlotInventory(Catalog(), -1));
        var inventory = new SlotInventory(Catalog(), 3);
        Assert.Equal(3, inventory.Capacity);
        Assert.Equal(0, inventory.OccupiedSlotCount);
        Assert.All(inventory.CreateSnapshot(), stack => Assert.True(stack.IsEmpty));
        Assert.Equal(30, inventory.GetAvailableCapacity(Wood));
    }

    [Fact]
    public void ZeroCapacity_RejectsMutationsAndAcceptsAnEmptySnapshot()
    {
        var inventory = new SlotInventory(Catalog(), 0);
        Assert.False(inventory.TryAdd(Wood, 1));
        Assert.False(inventory.TryRemove(Wood, 1));
        Assert.True(inventory.TryAdd(default(ItemStack)));
        Assert.True(inventory.TryRestore(ReadOnlySpan<ItemStack>.Empty));
        Assert.Equal(0, inventory.GetQuantity(Wood));
        inventory.Clear();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void IndexedOperations_RejectOutOfBoundsBeforeMutating(int index)
    {
        var inventory = new SlotInventory(Catalog(), 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => inventory[index]);
        Assert.Throws<ArgumentOutOfRangeException>(() => inventory.TryInsertAt(index, new ItemStack(Wood, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => inventory.TryTakeAt(index, 1, out _));
        Assert.Equal(0, inventory.OccupiedSlotCount);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void AggregateOperations_RequirePositiveQuantities(long quantity)
    {
        var catalog = Catalog();
        var inventory = new SlotInventory(catalog, 2);
        var other = new SlotInventory(catalog, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => inventory.TryAdd(Wood, quantity));
        Assert.Throws<ArgumentOutOfRangeException>(() => inventory.TryRemove(Wood, quantity));
        Assert.Throws<ArgumentOutOfRangeException>(() => inventory.TryTransferTo(other, Wood, quantity));
        Assert.Equal(0, inventory.OccupiedSlotCount);
        Assert.Equal(0, other.OccupiedSlotCount);
    }

    [Fact]
    public void Operations_RejectUnknownItemsWithoutMutating()
    {
        var inventory = new SlotInventory(Catalog(), 2);
        var missing = new ItemId(100);
        Assert.Throws<ArgumentException>(() => inventory.GetQuantity(default));
        Assert.Throws<ArgumentException>(() => inventory.GetAvailableCapacity(missing));
        Assert.Throws<ArgumentException>(() => inventory.TryAdd(missing, 1));
        Assert.Throws<ArgumentException>(() => inventory.TryRemove(missing, 1));
        Assert.Throws<ArgumentException>(() => inventory.TryInsertAt(0, new ItemStack(missing, 1)));
        Assert.Equal(0, inventory.OccupiedSlotCount);
    }

    [Fact]
    public void Add_FillsMatchingPartialStacksBeforeEarlierEmptySlots()
    {
        var inventory = new SlotInventory(Catalog(), 5);
        Assert.True(inventory.TryInsertAt(2, new ItemStack(Wood, 7)));
        Assert.True(inventory.TryInsertAt(3, new ItemStack(Stone, 2)));
        Assert.True(inventory.TryInsertAt(4, new ItemStack(Wood, 4)));
        Assert.True(inventory.TryAdd(Wood, 15));
        Assert.Equal(new ItemStack(Wood, 6), inventory[0]);
        Assert.True(inventory[1].IsEmpty);
        Assert.Equal(new ItemStack(Wood, 10), inventory[2]);
        Assert.Equal(new ItemStack(Stone, 2), inventory[3]);
        Assert.Equal(new ItemStack(Wood, 10), inventory[4]);
        Assert.Equal(4, inventory.OccupiedSlotCount);
        Assert.Equal(26, inventory.GetQuantity(Wood));
        Assert.Equal(14, inventory.GetAvailableCapacity(Wood));
    }

    [Fact]
    public void Add_InsufficientCapacityIsAtomicEvenWithPartialSpace()
    {
        var inventory = new SlotInventory(Catalog(), 2);
        Assert.True(inventory.TryAdd(Wood, 17));
        var before = inventory.CreateSnapshot();
        Assert.False(inventory.TryAdd(Wood, 4));
        Assert.False(inventory.TryAdd(Stone, 1));
        Assert.False(inventory.TryAdd(Wood, long.MaxValue));
        Assert.Equal(before, inventory.CreateSnapshot());
        Assert.Equal(2, inventory.OccupiedSlotCount);
    }

    [Fact]
    public void Add_AnOversizedStackCanSplitAcrossSlots()
    {
        var inventory = new SlotInventory(Catalog(), 3);
        Assert.True(inventory.TryAdd(new ItemStack(Wood, 25)));
        Assert.Equal(new[] { new ItemStack(Wood, 10), new ItemStack(Wood, 10), new ItemStack(Wood, 5) }, inventory.CreateSnapshot());
    }

    [Fact]
    public void Remove_UsesAscendingSlotsAndPreservesOtherItems()
    {
        var inventory = new SlotInventory(Catalog(), 4);
        Assert.True(inventory.TryInsertAt(0, new ItemStack(Wood, 4)));
        Assert.True(inventory.TryInsertAt(1, new ItemStack(Stone, 3)));
        Assert.True(inventory.TryInsertAt(2, new ItemStack(Wood, 10)));
        Assert.True(inventory.TryRemove(Wood, 7));
        Assert.True(inventory[0].IsEmpty);
        Assert.Equal(new ItemStack(Stone, 3), inventory[1]);
        Assert.Equal(new ItemStack(Wood, 7), inventory[2]);
        Assert.Equal(2, inventory.OccupiedSlotCount);
        var before = inventory.CreateSnapshot();
        Assert.False(inventory.TryRemove(Wood, 8));
        Assert.False(inventory.TryRemove(Wood, long.MaxValue));
        Assert.Equal(before, inventory.CreateSnapshot());
        Assert.True(inventory.TryRemove(Wood, 7));
        Assert.Equal(1, inventory.OccupiedSlotCount);
    }

    [Fact]
    public void IndexedInsertAndTake_RespectLimitsAndResetEmptyIdentity()
    {
        var inventory = new SlotInventory(Catalog(), 2);
        Assert.True(inventory.TryInsertAt(0, default));
        Assert.False(inventory.TryInsertAt(0, new ItemStack(Wood, 11)));
        Assert.True(inventory.TryInsertAt(0, new ItemStack(Wood, 8)));
        Assert.False(inventory.TryInsertAt(0, new ItemStack(Stone, 1)));
        Assert.False(inventory.TryInsertAt(0, new ItemStack(Wood, 3)));
        Assert.True(inventory.TryInsertAt(0, new ItemStack(Wood, 2)));
        Assert.False(inventory.TryTakeAt(0, 11, out var missing));
        Assert.Equal(default, missing);
        Assert.True(inventory.TryTakeAt(0, 4, out var taken));
        Assert.Equal(new ItemStack(Wood, 4), taken);
        Assert.Equal(new ItemStack(Wood, 6), inventory[0]);
        Assert.True(inventory.TryTakeAt(0, 6, out taken));
        Assert.Equal(new ItemStack(Wood, 6), taken);
        Assert.Equal(default, inventory[0]);
        Assert.False(inventory.TryTakeAt(0, 1, out _));
        Assert.Equal(0, inventory.OccupiedSlotCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => inventory.TryTakeAt(0, 0, out _));
    }

    [Fact]
    public void QuantitiesAboveIntMax_AreCountedAndTransferredWithoutOverflow()
    {
        var item = new ItemId(uint.MaxValue);
        var catalog = new ItemCatalog(new[] { new ItemDefinition(item, int.MaxValue) });
        var source = new SlotInventory(catalog, 3);
        var destination = new SlotInventory(catalog, 3);
        long capacity = 3L * int.MaxValue;
        Assert.Equal(capacity, source.GetAvailableCapacity(item));
        Assert.True(source.TryAdd(item, capacity));
        Assert.Equal(capacity, source.GetQuantity(item));
        Assert.Equal(0, source.GetAvailableCapacity(item));
        Assert.True(source.TryTransferTo(destination, item, capacity - 1));
        Assert.Equal(1, source.GetQuantity(item));
        Assert.Equal(capacity - 1, destination.GetQuantity(item));
        Assert.True(destination.TryRemove(item, capacity - 1));
        Assert.Equal(0, destination.OccupiedSlotCount);
        Assert.All(destination.CreateSnapshot(), stack => Assert.Equal(default, stack));
    }

    [Fact]
    public void Transfer_ChangesBothInventoriesOnlyWhenTheWholeRequestFits()
    {
        var catalog = Catalog();
        var source = new SlotInventory(catalog, 3);
        var destination = new SlotInventory(catalog, 2);
        Assert.True(source.TryAdd(Wood, 25));
        Assert.True(destination.TryAdd(Wood, 17));
        var sourceBefore = source.CreateSnapshot();
        var destinationBefore = destination.CreateSnapshot();
        Assert.False(source.TryTransferTo(destination, Wood, 4));
        Assert.False(source.TryTransferTo(destination, Wood, 26));
        Assert.Equal(sourceBefore, source.CreateSnapshot());
        Assert.Equal(destinationBefore, destination.CreateSnapshot());
        Assert.True(source.TryTransferTo(destination, Wood, 3));
        Assert.Equal(22, source.GetQuantity(Wood));
        Assert.Equal(20, destination.GetQuantity(Wood));
        Assert.Equal(3, source.OccupiedSlotCount);
        Assert.Equal(2, destination.OccupiedSlotCount);
    }

    [Fact]
    public void Transfer_RequiresSharedCatalogIdentityAndTreatsSelfTransferAsANoOp()
    {
        var source = new SlotInventory(Catalog(), 2);
        var equivalentCatalog = new SlotInventory(Catalog(), 2);
        Assert.True(source.TryAdd(Wood, 14));
        var before = source.CreateSnapshot();
        Assert.Throws<ArgumentNullException>(() => source.TryTransferTo(null!, Wood, 1));
        Assert.Throws<ArgumentException>(() => source.TryTransferTo(equivalentCatalog, Wood, 1));
        Assert.True(source.TryTransferTo(source, Wood, 14));
        Assert.False(source.TryTransferTo(source, Wood, 15));
        Assert.Equal(before, source.CreateSnapshot());
        Assert.Equal(0, equivalentCatalog.OccupiedSlotCount);
    }

    [Fact]
    public void Restore_ValidatesTheEntireSnapshotBeforeReplacingAnySlot()
    {
        var inventory = new SlotInventory(Catalog(), 3);
        Assert.True(inventory.TryAdd(Wood, 12));
        var before = inventory.CreateSnapshot();
        Assert.False(inventory.TryRestore(new ItemStack[] { default }));
        Assert.False(inventory.TryRestore(new[] { new ItemStack(Stone, 2), default, new ItemStack(Wood, 11) }));
        Assert.False(inventory.TryRestore(new[] { new ItemStack(Stone, 2), default, new ItemStack(new ItemId(99), 1) }));
        Assert.Equal(before, inventory.CreateSnapshot());
        Assert.True(inventory.TryRestore(new[] { default, new ItemStack(Stone, 3), new ItemStack(Tool, 1) }));
        Assert.Equal(2, inventory.OccupiedSlotCount);
        Assert.Equal(0, inventory.GetQuantity(Wood));
        Assert.Equal(3, inventory.GetQuantity(Stone));
        Assert.True(inventory.TryRestore(inventory.CreateSnapshot()));
        Assert.Equal(2, inventory.OccupiedSlotCount);
    }

    [Fact]
    public void Restore_RejectsMalformedBlittableStacksFromExternalByteReaders()
    {
        var inventory = new SlotInventory(Catalog(), 2);
        Assert.True(inventory.TryAdd(Wood, 4));
        var before = inventory.CreateSnapshot();
        Span<int> fields = stackalloc int[] { 1, -1, 0, 0 };
        Assert.False(inventory.TryRestore(MemoryMarshal.Cast<int, ItemStack>(fields)));
        fields[1] = 0;
        Assert.False(inventory.TryRestore(MemoryMarshal.Cast<int, ItemStack>(fields)));
        Assert.Equal(before, inventory.CreateSnapshot());
    }

    [Fact]
    public void StackInsertion_RejectsMalformedRawValuesWithoutMutating()
    {
        var inventory = new SlotInventory(Catalog(), 1);
        Assert.True(inventory.TryAdd(Wood, 3));
        Span<int> fields = stackalloc int[] { 1, -1 };
        var negative = MemoryMarshal.Cast<int, ItemStack>(fields)[0];
        Assert.Throws<ArgumentException>(() => inventory.TryAdd(negative));
        Assert.Throws<ArgumentException>(() => inventory.TryInsertAt(0, negative));
        fields[1] = 0;
        var noncanonicalEmpty = MemoryMarshal.Cast<int, ItemStack>(fields)[0];
        Assert.Throws<ArgumentException>(() => inventory.TryAdd(noncanonicalEmpty));
        Assert.Throws<ArgumentException>(() => inventory.TryInsertAt(0, noncanonicalEmpty));
        Assert.Equal(new ItemStack(Wood, 3), inventory[0]);
        Assert.Equal(1, inventory.OccupiedSlotCount);
    }

    [Fact]
    public void SlotCopiesAndIndexedValuesAreIndependentSnapshots()
    {
        var inventory = new SlotInventory(Catalog(), 1);
        Assert.True(inventory.TryAdd(Wood, 3));
        var snapshot = inventory[0];
        var view = inventory.CreateSnapshot();
        Assert.True(inventory.TryAdd(Wood, 2));
        Assert.Equal(3, snapshot.Quantity);
        Assert.Equal(3, view[0].Quantity);
        inventory.Clear();
        Assert.Equal(0, inventory.OccupiedSlotCount);
        Assert.Equal(new ItemStack(Wood, 3), view[0]);
        inventory.Clear();
        Assert.True(inventory.TryAdd(Tool, 1));
    }

    [Fact]
    public void DataOperations_AllocateNoManagedBytesAfterConstruction()
    {
        var catalog = Catalog();
        var source = new SlotInventory(catalog, 16);
        var destination = new SlotInventory(catalog, 16);
        var restored = new ItemStack[16];
        restored[0] = new ItemStack(Stone, 4);
        for (int warmup = 0; warmup < 100; warmup++) ExecuteOperations(source, destination, restored);
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int repeat = 0; repeat < 1_000; repeat++) ExecuteOperations(source, destination, restored);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(0, allocated);
    }

    private static void ExecuteOperations(SlotInventory source, SlotInventory destination, ItemStack[] restored)
    {
        source.TryRestore(restored);
        source.TryAdd(Wood, 12);
        source.GetQuantity(Wood);
        source.GetAvailableCapacity(Wood);
        source.TryTransferTo(destination, Wood, 12);
        destination.TryRemove(Wood, 12);
        source.TryInsertAt(3, new ItemStack(Tool, 1));
        source.TryTakeAt(3, 1, out _);
        source.Clear();
    }
}

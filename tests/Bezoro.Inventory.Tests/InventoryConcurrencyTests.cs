using System.Threading.Tasks;
using Xunit;

namespace Bezoro.Inventory.Tests;

public sealed class InventoryConcurrencyTests
{
    private static readonly ItemId Item = new(1);
    private static ItemCatalog Catalog() => new(new[] { new ItemDefinition(Item, 100_000) });

    [Fact]
    public void ConcurrentAdditionsAndRemovalsRetainEveryAcceptedUnit()
    {
        var inventory = new SlotInventory(Catalog(), 1);
        Parallel.For(0, 4, _ =>
        {
            for (int index = 0; index < 2_000; index++) Assert.True(inventory.TryAdd(Item, 1));
        });
        Assert.Equal(8_000, inventory.GetQuantity(Item));
        Parallel.For(0, 4, _ =>
        {
            for (int index = 0; index < 2_000; index++) Assert.True(inventory.TryRemove(Item, 1));
        });
        Assert.Equal(0, inventory.GetQuantity(Item));
        Assert.Equal(0, inventory.OccupiedSlotCount);
    }

    [Fact]
    public async Task OppositeTransfersJoinWithoutDeadlockingAndConserveQuantity()
    {
        var catalog = Catalog();
        var first = new SlotInventory(catalog, 1);
        var second = new SlotInventory(catalog, 1);
        Assert.True(first.TryAdd(Item, 10_000));
        Assert.True(second.TryAdd(Item, 10_000));
        using var ready = new System.Threading.Barrier(2);
        Task Move(SlotInventory source, SlotInventory destination) => Task.Factory.StartNew(() =>
        {
            Assert.True(ready.SignalAndWait(TimeSpan.FromSeconds(10)));
            for (int index = 0; index < 10_000; index++) source.TryTransferTo(destination, Item, 1);
        }, default, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await Task.WhenAll(Move(first, second), Move(second, first)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(20_000, first.GetQuantity(Item) + second.GetQuantity(Item));
        Assert.Equal(first[0].IsEmpty ? 0 : 1, first.OccupiedSlotCount);
        Assert.Equal(second[0].IsEmpty ? 0 : 1, second.OccupiedSlotCount);
    }

    [Fact]
    public void CopyToObservesOneCompleteRestoreAndSnapshotsRemainIndependent()
    {
        var inventory = new SlotInventory(Catalog(), 32);
        var first = Enumerable.Repeat(new ItemStack(Item, 1), inventory.Capacity).ToArray();
        var second = Enumerable.Repeat(new ItemStack(Item, 2), inventory.Capacity).ToArray();
        Assert.True(inventory.TryRestore(first));
        var snapshot = inventory.CreateSnapshot();
        Parallel.Invoke(() =>
        {
            for (int index = 0; index < 2_000; index++) Assert.True(inventory.TryRestore((index & 1) == 0 ? second : first));
        }, () =>
        {
            var copied = new ItemStack[inventory.Capacity];
            for (int index = 0; index < 2_000; index++)
            {
                inventory.CopyTo(copied);
                Assert.All(copied, stack => Assert.Equal(copied[0], stack));
            }
        });
        Assert.Equal(first, snapshot);
        Assert.Throws<ArgumentException>(() => inventory.CopyTo(new ItemStack[1]));
    }

    [Fact]
    public void WarmCopiesUseOnlyCallerStorage()
    {
        var inventory = new SlotInventory(Catalog(), 8);
        var scratch = new ItemStack[8];
        for (int index = 0; index < 100; index++) inventory.CopyTo(scratch);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_000; index++) inventory.CopyTo(scratch);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

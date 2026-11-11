using BenchmarkDotNet.Attributes;

namespace Bezoro.Inventory.Benchmarks;

[MemoryDiagnoser]
public class InventoryBenchmarks
{
    private readonly ItemId _item = new(1);
    private SlotInventory _source = null!;
    private SlotInventory _destination = null!;
    private ItemStack[] _snapshot = null!;

    [Params(16, 128)]
    public int Slots { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var catalog = new ItemCatalog(new[] { new ItemDefinition(_item, 100) });
        _source = new SlotInventory(catalog, Slots);
        _destination = new SlotInventory(catalog, Slots);
        _source.TryAdd(_item, Slots * 50L);
        _snapshot = _source.CreateSnapshot();
    }

    [Benchmark]
    public long CountQuantity() => _source.GetQuantity(_item);

    [Benchmark]
    public long CountCapacity() => _source.GetAvailableCapacity(_item);

    [Benchmark]
    public bool AddRemovePair()
    {
        bool added = _source.TryAdd(_item, 23);
        return added && _source.TryRemove(_item, 23);
    }

    [Benchmark]
    public bool TransferRoundTrip()
    {
        bool transferred = _source.TryTransferTo(_destination, _item, 37);
        return transferred && _destination.TryTransferTo(_source, _item, 37);
    }

    [Benchmark]
    public bool RestoreSlots() => _source.TryRestore(_snapshot);
}

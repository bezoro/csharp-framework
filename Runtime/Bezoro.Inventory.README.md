# Bezoro.Inventory

Fixed-capacity item storage for bags, chests, loot, and application-owned game data. The assembly targets .NET 10 and .NET Standard 2.1 and references no engine or other framework module.

```csharp
using Bezoro.Inventory;

var wood = new ItemId(1);
var axe = new ItemId(2);
var catalog = new ItemCatalog(new[]
{
    new ItemDefinition(wood, maximumStackSize: 99),
    new ItemDefinition(axe, maximumStackSize: 1)
});
var bag = new SlotInventory(catalog, capacity: 24);
var chest = new SlotInventory(catalog, capacity: 48);

if (bag.TryAdd(wood, quantity: 120))
    bag.TryTransferTo(chest, wood, quantity: 40);

ItemStack firstSlot = bag[0];
long totalWood = bag.GetQuantity(wood);
```

## Data and identity

`ItemId` contains an application-defined positive `uint`. Zero is reserved for empty slots. Identifiers carry no catalog identity; an application owns their meaning and persistence. An `ItemDefinition` pairs an identifier with a positive per-slot stack limit. A catalog copies definitions in their input order and rejects duplicate identifiers and default definitions. Its copied storage and lookup can be read concurrently.

`ItemStack` is an 8-byte readonly value. Its constructor requires a valid identifier and a positive `int` quantity. `default(ItemStack)` is the canonical empty stack, with identifier and quantity both zero. Constructing a stack does not consult a catalog; inserting it validates that catalog's rules.

Inventories exchanging items must share the same `ItemCatalog` instance. Two separately constructed catalogs with equivalent definitions still represent different catalogs for transfers. This prevents an item identifier from silently changing meaning during a transfer. `TryRestore` consumes values rather than a catalog-owned snapshot; the application must select the correct catalog and schema when loading saved data.

## Operations and failures

Every mutation that can run out of space is all-or-nothing. A false result leaves all affected inventories unchanged. Invalid indexes, nonpositive aggregate quantities, unknown identifiers, malformed stack arguments, null arguments, and transfer catalog mismatches throw before mutation.

| Operation | Contract |
| --- | --- |
| `TryAdd(item, quantity)` | Fill matching partial stacks in ascending slot order, then empty slots in ascending order. An aggregate can split across slots. |
| `TryAdd(stack)` | Add the stack's whole quantity, potentially across several slots. An empty stack succeeds without mutation. |
| `TryRemove(item, quantity)` | Remove from matching slots in ascending order. Empty results use the canonical default value. |
| `TryInsertAt(index, stack)` | Merge into one matching or empty slot only if the complete stack fits its limit. |
| `TryTakeAt(index, quantity, out stack)` | Extract the complete requested quantity from one slot. Failure returns an empty output. |
| `TryTransferTo(destination, item, quantity)` | Validate source quantity and destination capacity, then update both. A self-transfer succeeds as a no-op only if the requested quantity exists. |
| `TryRestore(snapshot)` | Validate every slot, then replace all slots. Wrong lengths, unknown items, malformed empty stacks, negative quantities, and oversized per-slot quantities return false. Input may alias the borrowed current slot view. |
| `Clear()` | Empty every slot while retaining storage. |

Indexed insertion/take and occupied-slot count use constant work. Aggregate queries and mutations scan bounded slot storage; a transfer scans its source and destination. No per-item count cache or per-inventory dictionary is retained.

Aggregate quantities and capacity sums use `long`; each individual slot uses `int`. This supports inventories containing more than `int.MaxValue` items without wrapping. The maximum mathematical total is `int.MaxValue × int.MaxValue`, which fits in `long`. Requests up to `long.MaxValue` fail normally when they exceed available quantity or capacity.

## Memory, threading, and persistence

Construction allocates one `ItemStack[capacity]`, about eight bytes per slot plus its array header. Zero capacity uses shared empty storage. Catalog storage is allocated once and can be shared. The lookup retains array indexes rather than duplicate definition values. Successful operations and expected capacity failures allocate no managed storage after construction. Exception creation and application-owned snapshot copies allocate as usual.

An indexer returns a value snapshot. `Slots` returns a borrowed read-only span over all slots, including empty slots. Mutations change that view. Use `Slots.CopyTo(destination)` to retain a snapshot in application-owned storage, and keep readers externally synchronized with mutations. No slot handles, callbacks, events, or background tasks are retained.

A mutable inventory needs one owner or external synchronization. A transfer requires exclusive access to both inventories until it completes. Independent inventories can run on separate workers, while a shared immutable catalog supports concurrent reads. Here, atomic means that a failed single operation makes no partial data change; it does not mean lock-free thread safety.

Save item identifiers and quantities through the application's serializer and schema policy. `TryRestore` validates them against the selected catalog before changing the live inventory. The module does not interpret item metadata, instantiate engine assets, manage equipment, choose pickup rules, or perform crafting.

## Validation and benchmarks

Tests cover limits, catalog ownership, full-request failure, transfer conservation, aliased restore, borrowed views, zero capacity, quantities above Int32, and zero managed allocation in warmed data operations. A seeded model plans changes against copied arrays before committing; it compares 40,000 mutations against the preflight-based implementation.

```sh
dotnet test tests/Bezoro.Inventory.Tests/Bezoro.Inventory.Tests.csproj -c Release
dotnet run -c Release --project benchmarks/Bezoro.Inventory.Benchmarks -- --filter '*InventoryBenchmarks*'
```

The benchmark project measures count queries, add/remove pairs, transfer round trips, and restore with preallocated storage. Pair and round-trip results describe both operations together. Engine player and application frame-budget claims require measurements on their target runtime.


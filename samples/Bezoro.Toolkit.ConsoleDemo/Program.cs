using System.Numerics;
using Bezoro.Core.Binary;
using Bezoro.Core.Collections;
using Bezoro.Inventory;
using Bezoro.Spatial;
using Bezoro.StateMachines;
using Bezoro.UnityApiSmoke;

var coin = new ItemId(1);
var catalog = new ItemCatalog(new[] { new ItemDefinition(coin, maximumStackSize: 10) });
var backpack = new SlotInventory(catalog, capacity: 2);
var stash = new SlotInventory(catalog, capacity: 1);
var questSetup = new StateMachineBuilder();
var gatheringState = questSetup.State();
var returningState = questSetup.State();
var finishedState = questSetup.State();
var pickupEvent = questSetup.Signal();
var deposit = questSetup.Signal();
questSetup.StartIn(gatheringState);
questSetup.Transition(from: gatheringState, on: pickupEvent, to: returningState);
questSetup.Transition(from: returningState, on: deposit, to: finishedState);
var quest = questSetup.Build();
StateId returning = questSetup.Resolve(returningState), finished = questSetup.Resolve(finishedState);
SignalId collected = questSetup.Resolve(pickupEvent), deposited = questSetup.Resolve(deposit);
StateId state = quest.InitialState;
var world = new PointSpatialIndex<ItemId>(
    new Bounds2D(Vector2.Zero, new Vector2(16, 16)), cellSize: 2, capacity: 8, maximumCells: 64);
world.Add(new Vector2(1, 1), coin);
world.Add(new Vector2(2, 1), coin);
world.Add(new Vector2(2, 2), coin);
world.Add(new Vector2(12, 12), coin);
Span<SpatialHit<ItemId>> nearby = stackalloc SpatialHit<ItemId>[8];
SpatialQueryResult query = world.QueryCircle(new Vector2(2, 2), radius: 2, nearby);
Require(query.IsSuccess, "The reusable hit buffer must hold this bounded index.");
// This host owns both mutable objects for the whole pickup phase.
for (int i = 0; i < query.WrittenCount; i++)
{
    SpatialHit<ItemId> pickup = nearby[i];
    if (backpack.TryAdd(pickup.Value, 1))
        Require(world.Remove(pickup.Handle), "The single-owner pickup snapshot must still be live.");
}
Require(backpack.GetQuantity(coin) == 3, "Three nearby coins must have been collected.");
state = quest.Step(state, collected).CurrentState;
Require(backpack.TryTransferTo(stash, coin, 3), "The complete deposit must fit.");
state = quest.Step(state, deposited).CurrentState;
var completed = new BitSet(quest.StateCount);
completed.Set(state.Value);
Require(state == finished && completed[finished.Value], "The quest must finish after depositing.");

// Schema 1: state:int32, slotCount:int32, repeated (item:uint32, quantity:int32).
Span<byte> payload = stackalloc byte[8 + 8 * stash.Capacity];
var writer = new BinarySpanWriter(payload);
Require(writer.TryWriteInt32(state.Value) && writer.TryWriteInt32(stash.Capacity), "Checkpoint header must fit.");
foreach (var stack in stash.Slots)
    Require(writer.TryWriteUInt32(stack.Item.Value) && writer.TryWriteInt32(stack.Quantity), "Checkpoint slot must fit.");

var reader = new BinarySpanReader(writer.WrittenSpan);
Require(reader.TryReadInt32(out int loadedState) && loadedState >= 0 && loadedState < quest.StateCount,
    "Invalid saved quest state.");
Require(reader.TryReadInt32(out int slotCount) && slotCount == stash.Capacity, "Invalid saved slot count.");
var restoredSlots = new ItemStack[slotCount];
for (int i = 0; i < restoredSlots.Length; i++)
{
    bool hasItem = reader.TryReadUInt32(out uint item);
    bool hasQuantity = reader.TryReadInt32(out int quantity);
    Require(hasItem && hasQuantity, "Truncated saved slot.");
    restoredSlots[i] = item == 0 && quantity == 0 ? default : new ItemStack(new ItemId(item), quantity);
}
Require(reader.Remaining == 0, "Unexpected checkpoint tail.");
var restored = new SlotInventory(catalog, slotCount);
Require(restored.TryRestore(restoredSlots), "The saved slots must match catalog limits.");
Require(restored.GetQuantity(coin) == 3 && new StateId(loadedState) == finished, "Checkpoint restore mismatch.");
Require(FoundationConsumer.ExerciseDataApis() > 0 && ToolkitConsumer.ExerciseDataApis() > 0,
    "The C# 9 consumers must run successfully.");
Console.WriteLine($"Collected {query.WrittenCount} coins; {world.Count} distant pickup remains.");
Console.WriteLine($"Quest state {state.Value}; stash {restored.GetQuantity(coin)} coins; payload {writer.Position} bytes.");
Console.WriteLine("Both C# 9 API consumers executed successfully.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

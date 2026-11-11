# HealthSystem

ECS-native health simulation with deferred mutation requests and event queue integration.

## Types

| Type                    | Description                                                                 |
|-------------------------|-----------------------------------------------------------------------------|
| `Health`                | ECS component for base + excess health values.                              |
| `HealthMutationRequest` | Deferred mutation command component (`Damage`, `DirectDamage`, `Heal`, `IncreaseHealth`, `SetMax`). |
| `HealthMutationKind`    | Request operation enum consumed by `HealthSystem`.                          |
| `HealthSystem`          | ECS `ISystem` that consumes requests and mutates `Health`.                  |
| `HealthChangedEvent`    | Event payload with before/after values and signed deltas.                   |
| `HealthEventsResource`  | Pull-based world event queue for health mutation events.                    |
| `HealthWorldExtensions` | Queue helpers for damage/heal variants and max-health updates.               |

## Quick start

```csharp
using Bezoro.ECS.Services;
using Bezoro.GameSystems.HealthSystem.Extensions;
using Bezoro.GameSystems.HealthSystem.Services;
using Bezoro.GameSystems.HealthSystem.Types;

using var world = new World();
var healthSystem = new HealthSystem();
world.AddSystem(healthSystem);

var entity = world.Spawn(new Health(max: 100u, current: 100u, excessCurrent: 10u, excessMax: 25u));

world.QueueHealthDamage(entity, 15u);
world.QueueHealthHeal(entity, 5u);
world.QueueHealthIncreaseHealth(entity, 20u);
world.Tick(0f);

var health = world.Read<Health>(entity);
Console.WriteLine($"{health.Current}/{health.Max} + {health.ExcessCurrent} excess");

ref var events = ref world.WriteResource<HealthEventsResource>();
while (events.TryDequeue(out var evt))
{
    Console.WriteLine($"{evt.Kind}: dCurrent={evt.DeltaCurrent}, dExcess={evt.DeltaExcess}");
}
```

## API reference

### Health

| Member             | Description                                              |
|--------------------|----------------------------------------------------------|
| `Current`          | Base health value.                                       |
| `Max`              | Base health cap.                                         |
| `ExcessCurrent`    | Excess health value (shield/overheal pool).              |
| `ExcessMax`        | Excess health cap.                                       |
| `EffectiveCurrent` | `Current + ExcessCurrent`, saturated at `uint.MaxValue`. |
| `BasePercentage`   | Base pool percentage.                                    |
| `ExcessPercentage` | Excess pool percentage.                                  |
| `TotalPercentage`  | Combined base + excess percentage, using wide sums without a temporary array. |

### MaxValueUpdateMode

| Value                | Description                                                |
|----------------------|------------------------------------------------------------|
| `ClampCurrent`       | Clamp current health to new max (default).                 |
| `PreservePercentage` | Scale current health proportionally to maintain percentage |

### HealthWorldExtensions

| Method                                                        | Description                         |
|---------------------------------------------------------------|-------------------------------------|
| `QueueHealthDamage(World, Entity, uint)`                     | Queues excess-first damage.         |
| `QueueHealthDirectDamage(World, Entity, uint)`               | Queues direct base-health damage.   |
| `QueueHealthHeal(World, Entity, uint)`                       | Queues base-only heal (no overflow).|
| `QueueHealthIncreaseHealth(World, Entity, uint)`             | Queues heal with overflow to excess.|
| `QueueSetHealthMax(World, Entity, uint, MaxValueUpdateMode)` | Queues a max-health change request. |

## Runtime notes

- Helpers create request entities immediately and defer the health mutation until the system update. Call them on the world thread; they are not thread-safe producer queues.
- `HealthSystem` consumes request entities in ECS query traversal order and despawns them after processing. Do not rely on a general FIFO guarantee across archetype changes or entity-storage reuse.
- Request construction rejects undefined mutation and max-update modes before a request entity is created. Max-health updates compute the new current value before changing either field, so a rejected update cannot leave a partially changed cap.
- Damage supports both excess-first (`Damage`) and base-only (`DirectDamage`).
- Healing supports both base-only (`Heal`) and overflow-to-excess (`IncreaseHealth`).
- Events are emitted both through `HealthSystem.Changed` and `HealthEventsResource`.

## Ownership and integration

Health fields are mutable. The constructor clamps initial pools, and mutation helpers apply their documented clamping and saturation rules. Callers writing fields directly must preserve their own invariants. Callbacks run synchronously on the world thread. The event invocation catches exceptions, but a throwing subscriber can stop later subscribers in that invocation; queued event consumption is independent. Acquire `HealthEventsResource` under world ownership, then share that queue with worker or engine-adapter consumers. Queue operations are synchronized and support optional preallocation and backlog limits under the [shared event-queue contract](../README.md#runtime-behavior).

This subsystem shares the [module targets and engine integration](../README.md). Run its .NET 10 tests with:

```bash
dotnet test tests/Bezoro.GameSystems.Tests/Bezoro.GameSystems.Tests.csproj -c Release -f net10.0 --filter "FullyQualifiedName~HealthSystem"
```

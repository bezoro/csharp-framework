# InputSystem

External input ingestion for ECS movement, with hold-last behavior and sequence-based ordering.

## Types

| Type                     | Description                                                                |
|--------------------------|----------------------------------------------------------------------------|
| `IInputCommandSink`      | External producer contract for enqueueing immutable `InputCommand` values. |
| `InputCommand`           | Immutable movement command (`ControlId`, axis values, `Sequence`).         |
| `InputCommandQueue`      | Thread-safe ingress with world-thread latest-per-control snapshots.                  |
| `InputControl`           | Component that maps an entity to a logical input channel id.               |
| `MovementIntent`         | Component storing currently applied movement intent.                       |
| `MovementInputSettings`  | Component with per-entity movement speed and hold duration.                |
| `InputIngestionSystem`   | `FixedTick` + `Input` stage system that drains queued commands.            |
| `IntentToVelocitySystem` | `FixedTick` + `PreTick` stage system that converts intent to `Velocity`.   |
| `InputWorldExtensions`   | Convenience methods for queue resource setup and pipeline registration.    |

## Quick start

```csharp
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Bezoro.GameSystems.InputSystem.Extensions;
using Bezoro.GameSystems.InputSystem.Types;
using Bezoro.GameSystems.MovementSystem.Services;
using Bezoro.GameSystems.MovementSystem.Types;

using var world = new World();
world.AddMovementInputPipeline();
world.AddSystem(new MovementSystem(), Stage.Tick);

var queue = world.GetOrCreateInputCommandQueue();

var player = world.Spawn(
    new Position(),
    new Velocity(),
    new InputControl { ControlId = 1 },
    new MovementIntent()
);
world.Add(player, new MovementInputSettings { Speed = 5f, HoldDurationSeconds = 0.15f });

queue.Enqueue(new InputCommand(controlId: 1, moveX: 1f, moveY: 0f, moveZ: 0f, sequence: 1));
world.FixedTick(0.02f);
```

## API reference

### InputCommandQueue

| Member                                         | Description                                                  |
|------------------------------------------------|--------------------------------------------------------------|
| `Enqueue(in InputCommand)`                     | Adds an external command from any thread.                    |
| `Enqueue(int,float,float,float,ulong)`         | Convenience overload for command creation + enqueue.         |
| `SimulationTimeSeconds` | Double-precision accumulated simulation time used for aging. |
| `PendingCommandCount` | Approximate producer backlog. |
| `TrackedControlCount` | Retained control snapshots, read on the world thread. |
| `ForgetControl(int)` | Removes a snapshot and its sequence on the world thread. Queued commands remain. |

### InputWorldExtensions

| Method                                | Description                                                  |
|---------------------------------------|--------------------------------------------------------------|
| `GetOrCreateInputCommandQueue(World)` | Returns existing input queue resource or creates one.        |
| `AddMovementInputPipeline(World)`     | Registers ingestion and intent systems with correct stages.  |

## Ingestion budget

Pass `maxCommandsPerUpdate` to `AddMovementInputPipeline` or `InputIngestionSystem` to bound dequeues, including stale commands. Zero pauses ingestion while simulation time still advances. Negative budgets throw before pipeline registration. The default drains until the queue is observed empty, so sustained producers can prolong an update. A budget does not cap retained queue memory.

## Hold-last behavior

- Each control id keeps the highest accepted `Sequence`. Equal or lower sequences are ignored and do not refresh the command's age. Sequence zero is valid for the first command; sequence wraparound is not implemented.
- A command is timestamped when ingestion drains it, not when a producer enqueues it. Time spent in the producer queue is not included in its hold age.
- `IntentToVelocitySystem` keeps that command active while command age is less than or equal to `HoldDurationSeconds`. A zero hold applies during its ingestion update only when simulation time advances between updates.
- When the hold window expires without newer input, `MovementIntent` and `Velocity` are zeroed.

## Runtime notes

- Producers and consumers are isolated: external threads only enqueue immutable commands.
- Queue inspection, time advancement, snapshot consumption, ECS writes, and world disposal belong to the world update thread. Only `Enqueue` is a producer-thread operation.
- Ingress and the latest-per-control map have no capacity or expiration cleanup policy. Use `ForgetControl` after a producer stops to release its snapshot and allow a sequence restart. Queued commands can recreate that snapshot. Dictionary capacity remains reserved for reuse. Control producer backlog at the application boundary.
- Commands reject negative control IDs and non-finite axes. Invalid mutable movement settings or velocity multiplication overflow neutralize that entity's complete intent and velocity vector; other entities continue updating.
- Timestamps and `SimulationTimeSeconds` use double precision, avoiding float-resolution loss that can otherwise keep short inputs held after long runtimes.
- `World.FixedTick` rejects non-finite or negative deltas. Direct calls to `InputIngestionSystem.Update` skip time advancement for those values.
- Input sequence numbers order commands for one control. They do not make the whole simulation deterministic across different producer timing or platforms.
- Stage ordering is explicit: input ingestion (`Input`) -> velocity conversion (`PreTick`) -> movement (`Tick`).

## Validation

```bash
dotnet test tests/Bezoro.GameSystems.Tests/Bezoro.GameSystems.Tests.csproj -c Release -f net10.0 --filter "FullyQualifiedName~InputSystem"
```

These tests cover hold-window boundaries, stale sequences, numeric recovery, and expiry after long simulated uptime. See [the module README](../README.md) for targets and engine integration.

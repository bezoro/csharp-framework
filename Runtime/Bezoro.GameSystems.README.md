# Bezoro.GameSystems

ECS-native gameplay systems built on top of `Bezoro.ECS`, `Bezoro.Core`, and `Bezoro.Events`. The assembly groups reusable runtime pipelines that can be composed into game loops without coupling them to any specific engine.

## Included systems

- `ActivationSystem`: deferred callback activation with priority ordering and per-tick budget control
- `HealthSystem`: health state, damage, healing, and lifecycle-oriented health flow
- `InputSystem`: input command ingestion patterns for ECS-driven gameplay
- `StreamingSystem`: streaming-oriented system composition for chunk or world activation workflows
- `TimerSystem`: timer components and tick-based timer progression

## Quick start

```csharp
using Bezoro.ECS.Services;
using Bezoro.GameSystems.ActivationSystem.Extensions;
using Bezoro.GameSystems.ActivationSystem.Types;

using var world = new World();
world.SetResource(new ActivationConfig(maxActivationsPerTick: 4));
world.AddActivationPipeline();
```

## API reference

| Area | Primary APIs |
| --- | --- |
| Activation | `ActivationWorldExtensions`, `ActivationConfig`, `ActivationCommandQueue`, `ActivationHandle` |
| Health | `HealthWorldExtensions`, `Health`, `HealthMutationRequest`, `HealthChangedEvent` |
| Input | `InputWorldExtensions`, `InputCommandQueue`, `InputControl`, `MovementIntent` |
| Movement | `MovementSystem`, `Position`, `Velocity` |
| Streaming | `StreamingSystem`, `StreamingConfig`, `StreamState`, `StreamingStateChangedEvent` |
| Timer | `TimerWorldExtensions`, `Timer`, `TimerMode`, `TimerLifecycleEvent` |

## Runtime behavior

- Pipelines register ECS systems and resources through explicit `World` extension methods.
- Only ingress queues explicitly documented as thread-safe accept producer-thread calls. World APIs, resource access, and health/timer helpers run on the world update thread.
- Lifecycle events are stored in world resources so consumers can drain them during their own systems.
- World updates require finite, non-negative delta times. Unity hosts typically call `Tick(Time.deltaTime)` in `Update` and `FixedTick(Time.fixedDeltaTime)` in `FixedUpdate`; Godot hosts can call them from their corresponding process callbacks.
- Callbacks run on the world update thread unless the subsystem has an explicitly configured dispatcher. The framework does not move the complete simulation to the engine's main thread.
- Drain lifecycle queues regularly. A processing budget limits work per update and does not by itself bound producer backlog or memory use.
- Input timestamps and the public simulation clock use double precision so short hold windows still expire after long uptimes. Input ingestion has an explicit optional dequeue budget and supports releasing control snapshots on the world thread.
- Movement integrates every finite positive delta, including values below a comparison epsilon. It skips invalid direct-update deltas and leaves a complete position unchanged when integration would produce a non-finite component.
- Timer construction rejects non-finite values. The timer system skips advancement for non-finite values introduced through mutable timer fields, allowing recovery after those fields are repaired.

## Subsystem documentation

- [Activation](https://github.com/bezoro/framework/blob/db872cf8e25efa39f6fcb8b5dc7ef9955f9763ea/src/Bezoro.GameSystems/ActivationSystem/README.md)
- [Health](https://github.com/bezoro/framework/blob/db872cf8e25efa39f6fcb8b5dc7ef9955f9763ea/src/Bezoro.GameSystems/HealthSystem/README.md)
- [Input](https://github.com/bezoro/framework/blob/db872cf8e25efa39f6fcb8b5dc7ef9955f9763ea/src/Bezoro.GameSystems/InputSystem/README.md)
- [Streaming](https://github.com/bezoro/framework/blob/db872cf8e25efa39f6fcb8b5dc7ef9955f9763ea/src/Bezoro.GameSystems/StreamingSystem/README.md)
- [Timers](https://github.com/bezoro/framework/blob/db872cf8e25efa39f6fcb8b5dc7ef9955f9763ea/src/Bezoro.GameSystems/TimerSystem/README.md)

## Dependencies

- [Bezoro.Core](https://github.com/bezoro/framework/blob/db872cf8e25efa39f6fcb8b5dc7ef9955f9763ea/src/Bezoro.Core/README.md)
- [Bezoro.ECS](https://github.com/bezoro/framework/blob/db872cf8e25efa39f6fcb8b5dc7ef9955f9763ea/src/Bezoro.ECS/README.md)
- [Bezoro.Events](https://github.com/bezoro/framework/blob/db872cf8e25efa39f6fcb8b5dc7ef9955f9763ea/src/Bezoro.Events/README.md)

## Design notes

- Systems depend only on Bezoro framework contracts and remain independent of Unity or another engine.
- Mutable gameplay data is represented as ECS components or resources rather than hidden service state.
- Ingestion, processing, and dispatch are separate systems when a workflow crosses a queue boundary.
- Generated query specifications keep filtering and scheduler metadata compile-time visible.

## Migration

- `InputCommandQueue.SimulationTimeSeconds` and `StreamingStateChangedEvent.DistanceSquared` are doubles. Avoid narrowing casts in timing or distance decisions.
- Streaming configuration rejects non-finite thresholds and reference coordinates before changing state.
- Timer control calls preserve every accepted transition before an update. Event consumers now receive the complete ordered sequence.
- Invalid health mutation modes fail at request construction instead of during a partially applied update.

## Targets and validation

Targets .NET 10 and .NET Standard 2.1. Unity 6 consumes the precompiled .NET Standard assembly; see the [Unity package](https://github.com/bezoro/framework/blob/db872cf8e25efa39f6fcb8b5dc7ef9955f9763ea/unity-package/README.md) for installation. The systems do not depend on Unity or Godot types.

Use the SDK selected by [global.json](https://github.com/bezoro/framework/blob/db872cf8e25efa39f6fcb8b5dc7ef9955f9763ea/global.json):

```bash
dotnet build src/Bezoro.GameSystems/Bezoro.GameSystems.csproj -c Release
dotnet test tests/Bezoro.GameSystems.Tests/Bezoro.GameSystems.Tests.csproj -c Release -f net10.0
```

The tests exercise ECS composition and numeric behavior without launching an engine. They do not establish platform-specific frame budgets or AOT compatibility; validate those in the target player build.


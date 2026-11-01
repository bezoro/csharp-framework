# TimerSystem

ECS-native timers for cooldowns and scheduled gameplay logic.

## Types

| Type                   | Description                                                                              |
|------------------------|------------------------------------------------------------------------------------------|
| `Timer`                | Timer component (duration, elapsed, state, mode, id).                                    |
| `TimerSystem`          | ECS `ISystem` that advances running timers and emits lifecycle callbacks.                |
| `TimerLifecycle`       | Transition kind: `Started`, `Paused`, `Stopped`, `Finished`, `Resumed`, `Restarted`.     |
| `TimerLifecycleEvent`  | Event payload for callbacks and event queue consumers.                                   |
| `TimerEventsResource`  | World resource queue for pull-based lifecycle event consumption.                         |
| `TimerOwner`           | Optional owner link from timer entity to gameplay entity.                                |
| `TimerWorldExtensions` | Control helpers: `StartTimer`, `PauseTimer`, `StopTimer`, `ResumeTimer`, `RestartTimer`. |

## Quick start

```csharp
using Bezoro.ECS.Services;
using Bezoro.GameSystems.TimerSystem.Extensions;
using Bezoro.GameSystems.TimerSystem.Services;
using Bezoro.GameSystems.TimerSystem.Types;
using Timer = Bezoro.GameSystems.TimerSystem.Types.Timer;

using var world = new World();
var timers = new TimerSystem();
world.AddSystem(timers);

var timerEntity = world.Spawn(new Timer(
    timerId: 1001,
    durationSeconds: 3f,
    state: TimerState.Stopped,
    mode: TimerMode.Persistent));

world.StartTimer(timerEntity);

timers.Finished += evt => Console.WriteLine($"Timer {evt.TimerId} finished.");

world.Tick(1f);
world.Tick(1f);
world.Tick(1f);

ref var eventQueue = ref world.WriteResource<TimerEventsResource>();
while (eventQueue.TryDequeue(out var evt))
{
    Console.WriteLine($"{evt.Lifecycle} -> {evt.TimerId}");
}
```

## API reference

### `Timer`

| Member             | Description                                  |
|--------------------|----------------------------------------------|
| `TimerId`          | App-level timer identifier.                  |
| `DurationSeconds`  | Total duration in seconds.                   |
| `ElapsedSeconds`   | Elapsed time in seconds.                     |
| `State`            | `Running`, `Paused`, `Stopped`, `Completed`. |
| `Mode`             | `Persistent` or `OneShot`.                   |
| `RemainingSeconds` | Clamped remaining time in seconds.           |
| `Progress`         | Completion ratio from `0` to `1`.            |

### `TimerSystem` callbacks

| Event       | Trigger                          |
|-------------|----------------------------------|
| `Started`   | Stopped timer started.           |
| `Paused`    | Running timer paused.            |
| `Stopped`   | Running or paused timer stopped. |
| `Finished`  | Running timer reaches duration.  |
| `Resumed`   | Paused timer resumed.            |
| `Restarted` | Timer reset to zero and running. |

### `TimerWorldExtensions`

| Method                 | Description                                            |
|------------------------|--------------------------------------------------------|
| `StartTimer(Entity)`   | `Stopped -> Running` and queues `Started`.             |
| `PauseTimer(Entity)`   | `Running -> Paused` and queues `Paused`.               |
| `StopTimer(Entity)`    | `Running/Paused -> Stopped` and queues `Stopped`.      |
| `ResumeTimer(Entity)`  | `Paused -> Running` and queues `Resumed`.              |
| `RestartTimer(Entity)` | `* -> Running` + elapsed reset and queues `Restarted`. |

## Runtime behavior

- Timers are dedicated entities, enabling many timers per gameplay object.
- `TimerSystem` writes lifecycle events to `TimerEventsResource` for pull-based consumption.
- `TimerMode.OneShot` despawns timer entities when they finish.
- Construction requires a positive finite duration, finite initial elapsed time, and defined state and mode values. Finite elapsed values are clamped into the duration. Timer fields remain mutable; callers must maintain valid state when writing them directly.
- Non-finite mutable durations or elapsed values suspend advancement instead of falsely emitting `Finished`. Repair the values to resume processing. Direct system updates with non-finite or negative delta skip advancement; ordinary world updates reject invalid delta times before running systems.
- `Persistent` retains a completed timer entity; it does not repeat automatically. Call `RestartTimer` to reset and run it again.
- Control helpers change the component immediately on the world thread and preserve every accepted transition in call order. Each event snapshots state, elapsed time, duration, id, and owner at the control call. The system publishes the transitions before advancing timers. Transitions for destroyed entities or removed timer components are discarded.
- Pending control transitions use one world-owned queue created on the first accepted control call. The queue grows with the number of transitions waiting for an update and can allocate when it grows. It reserves no per-timer queue. Transitions requested from a callback wait until the next update; callbacks cannot make the current drain unlimited. Stop producers and stop world updates before world disposal.
- Callbacks execute synchronously on the world update thread. Exceptions do not escape the system's event invocation, but one throwing multicast subscriber can prevent later subscribers of that callback from running. The event is already queued in `TimerEventsResource`; drain that resource for independent consumption.
- World access, timer control, borrowed component refs, and event-queue draining require world-thread ownership. These helpers are not producer-thread queues.
- Timers accumulate elapsed time as floats. They suit gameplay cooldowns; they are not wall-clock timers or high-resolution scheduling guarantees.

## Migration

Multiple controls before one update now produce multiple events with their original states. Consumers that assumed only the final transition must handle the full accepted sequence. Callbacks for control transitions run before timer advancement across the world.

## Build

```bash
dotnet build src/Bezoro.GameSystems/Bezoro.GameSystems.csproj -c Release
dotnet test tests/Bezoro.GameSystems.Tests/Bezoro.GameSystems.Tests.csproj -c Release -f net10.0 --filter "FullyQualifiedName~TimerSystem"
```

See [the module README](../README.md) for .NET 10, .NET Standard 2.1, and Unity 6 integration.

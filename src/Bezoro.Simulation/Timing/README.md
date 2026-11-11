# Simulation timing

The timing APIs live in `Bezoro.Simulation.Timing`. They have no Unity, Godot, ECS, wall-clock, callback, or background-task dependency.

## Fixed-step simulation

`FixedStepClock` is a pure transformation of a configuration, a state, and elapsed time. The configuration, state, and result are readonly value types. The default state is the initial state; a configuration must be constructed explicitly.

```csharp
using Bezoro.Simulation.Timing;

var configuration = new FixedStepConfiguration(
    TimeSpan.FromMilliseconds(20),
    maxStepsPerAdvance: 4,
    backlogPolicy: FixedStepBacklogPolicy.Preserve);
var state = default(FixedStepState);

// Supply the host's elapsed time each frame.
var advance = FixedStepClock.AdvanceSeconds(configuration, state, elapsedSeconds);
for (int step = 0; step < advance.StepCount; step++)
{
    long simulationTick = state.Tick + step + 1;
    Simulate(simulationTick, configuration.StepDuration);
}
state = advance.State;
RenderInterpolated(advance.InterpolationAlpha);
```

The example uses an exact 50 Hz step. `TimeSpan` has 100 ns resolution, so rates whose periods cannot be expressed as an integer number of those units need an explicitly chosen duration. The clock does not claim that a rounded 60 Hz duration is exactly 1/60 second.

`State.Tick` counts simulated steps, not units of wall time. The host must execute the returned `StepCount` before retaining the returned state. Simulation failures and rollback are host decisions; the clock does not undo gameplay side effects.

| Policy | Behavior beyond `MaxStepsPerAdvance` |
| --- | --- |
| `Preserve` | Keeps whole steps in `AccumulatedTime`. `PendingStepCount` reports the backlog. Advancing with zero elapsed time can drain it in bounded batches. |
| `DropOverflow` | Discards excess whole steps and reports the duration in `DroppedTime`. The fractional remainder stays accumulated. |

`InterpolationAlpha` is accumulated time divided by step duration, clamped to one. With a whole preserved backlog it is one, preventing accidental extrapolation. With no backlog it represents the usual interpolation between simulation snapshots.

`Advance` uses integer `TimeSpan` units. When the execution budget does not bind, partitioning the same elapsed duration into frames yields the same final clock state. Under `Preserve`, draining the backlog also converges to that state. `DropOverflow` deliberately depends on frame partitioning because each advance has a separate work budget.

`AdvanceSeconds` rejects NaN, infinity, negative values, and values beyond `TimeSpan` range. It converts seconds to the nearest 100 ns with ties-to-even on both target frameworks. Rounding happens per call; splitting a double duration across calls can change the rounded total. Record exact elapsed `TimeSpan` values or simulation inputs for partition-exact replay.

Accumulated time and simulation tick overflow throw without mutating the input state. No duration is silently wrapped or saturated. To pause without draining an existing backlog, skip advancement altogether. The host chooses scaled or unscaled elapsed time.

## Bounded scheduling

`TickScheduler<T>` schedules data, not callbacks. A scheduler tick is an application simulation step, not a 100 ns `TimeSpan` unit. A typical host advances it once per fixed simulation step.

```csharp
using Bezoro.Simulation.Timing;

var effects = new TickScheduler<string>(capacity: 256);
var output = new ScheduledItem<string>[16];

// At 50 simulation steps per second this becomes due after half a second.
ScheduledHandle expiry = effects.Schedule("shield expired", delayTicks: 25);

SchedulerDrainResult due = effects.AdvanceTo(simulationTick, output);
for (int i = 0; i < due.Count; i++)
    ApplyEffect(output[i].Value);

// Leave remaining due work queued when the per-frame budget is exhausted.
// DrainDue(output) can retrieve another bounded batch without advancing time.
bool moreWorkRemains = due.HasMoreDue;

// Returns false after an item was drained, canceled, or cleared.
effects.TryCancel(expiry, out _);
```

Both `Schedule` and `ScheduleAt` have `Try` variants for capacity exhaustion. Invalid times still throw. `Schedule` accepts a delay relative to `CurrentTick`; `ScheduleAt` accepts an absolute tick. Zero delay is valid and makes the item available on the next drain.

Due items are ordered by due tick, then original insertion order. `TryRescheduleAt` retains that original insertion order when several items end up at the same due tick. `TryPeek` examines the earliest item, and `TryGet` reads one by handle.

Only the prefix `[0, result.Count)` of a supplied drain buffer is written. Empty buffers are valid and leave due work queued. Payload references previously placed in a reusable output array remain owned by that array until the host clears or overwrites them. Clear consumed output slots when payload lifetime matters.

The scheduler preallocates all its slots and its indexed binary heap:

| Operation | Cost |
| --- | --- |
| Construct | O(capacity) time and memory |
| Schedule, cancel, reschedule | O(log pending items) |
| Peek, lookup by handle | O(1) |
| Drain k items | O(k log pending items) |
| Clear | O(pending items) |

Successful operations allocate no per-item managed memory. Cancellation removes the heap entry immediately; canceled far-future items do not leave tombstones consuming capacity. Removed or cleared scheduler slots release their payload references.

`ScheduledHandle` is an opaque 16-byte value containing an owner identity, slot, and generation. Default, foreign, canceled, drained, and cleared handles are rejected. Ownership is distinct even across schedulers of different payload types. The uint generation of a slot never wraps: a slot is permanently retired when its last generation is released. `RetiredSlotCount` and `RemainingCapacity` expose this boundary. `Clear` does not reactivate retired slots. After insertion-order counter exhaustion, scheduling throws; `Clear` removes all pending items and restarts that counter. New owner identities also fail on exhaustion instead of wrapping.

`AdvanceTo` rejects time moving backward. Due-tick overflow is checked before enqueue. `Clear` invalidates pending handles and retains the current tick. A zero-capacity scheduler is valid and always full.

## Unity, Godot, ECS, and workers

For Unity, call `AdvanceSeconds` from `Update` with `Time.deltaTime` or `Time.unscaledDeltaTime`, according to the game's pause and time-scale policy. For Godot, supply the `delta` from a chosen process callback. The example owns its fixed step, so do not also advance the same simulation from an engine fixed-step callback. An engine-owned fixed loop can use `TickScheduler<T>` directly without the accumulator.

An ECS host can call `world.FixedTick((float)configuration.StepDuration.TotalSeconds)` for each returned step. Keep clock state and the scheduler in an explicitly owned resource or host object. The timing module itself does not depend on `World`.

Clock values can be copied between readers. Scheduler operations are synchronized per instance, so producers may schedule, reschedule or cancel while consumers drain into distinct buffers. Relative scheduling reads the current tick and reserves its slot atomically. Absolute ticks must still be at or beyond the tick current when the operation executes; concurrent advancement may make an earlier absolute tick invalid. Each drain returns an ordered prefix; separate consumers may process their returned prefixes in a different order. A single consumer is appropriate when processing order matters. Separate property reads are observations rather than one combined snapshot. Shared reference payloads need their own ownership policy. Run engine scene, rendering, and UI work through the engine adapter after consuming data.

Serialize the clock's configuration, tick, and accumulated duration to resume it exactly. Scheduler handles are in-process cancellation identities, not durable identifiers. To save pending events, persist application event data and intended ordering, then rebuild scheduling at load. The timing module does not serialize gameplay objects or claim deterministic physics across platforms.

## Verification

The tests cover exact time partitioning, overload policies, invalid time, tick and duration overflow, snapshots, heap ordering, cancellation, rescheduling, short drains, generation retirement, owner isolation, payload release, and thousands of differential operations against a reference ordered queue. A fixed eight-sample steady-state allocation regression checks successful scheduling/cancellation/draining without hiding a repeated per-operation allocation behind a byte allowance.

```sh
dotnet test tests/Bezoro.Simulation.Tests -c Release --filter FullyQualifiedName~Timing
dotnet run -c Release --project benchmarks/Bezoro.Simulation.Benchmarks -- --filter '*TimingBenchmarks*'
```

Benchmarks cover clock advancement and balanced scheduler batches. Profile the intended engine and player build before selecting a frame budget.

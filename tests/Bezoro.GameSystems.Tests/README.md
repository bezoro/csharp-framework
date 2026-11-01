# Bezoro.GameSystems.Tests

Behavior and contract tests for the ECS-native gameplay systems in `Bezoro.GameSystems`.

## Coverage

- Activation command ordering, cancellation, capacity, dispatch, handles, and world registration.
- Health mutation, clamping, lifecycle, event publication, and system scheduling.
- Input ingestion, movement intent, velocity updates, and command buffering.
- Movement-system position integration.
- Streaming transitions, capacity, event publication, and runtime state.
- Timer modes, lifecycle transitions, ownership, events, and world extensions.

## Run

```powershell
dotnet test tests/Bezoro.GameSystems.Tests/Bezoro.GameSystems.Tests.csproj -c Release -f net10.0 --verbosity minimal
```

## Scope

- Public gameplay-system APIs compose with a real `World`.
- Queue boundaries preserve documented ordering and capacity behavior.
- Generated ECS query specifications compile and execute against their intended components.
- Observable component, resource, and lifecycle-event state matches each subsystem contract.

## Conventions

- Assertions use xUnit and FluentAssertions.
- Test classes mirror the source subsystem folders.
- Tests exercise public behavior and world state rather than private implementation calls.
- Each test creates isolated world and queue state.

## Coverage notes

- These are unit-level composition tests; they do not require Unity or another engine.
- Concurrency tests coordinate work with bounded synchronization rather than timing-only assertions.

Tests use the .NET 10 SDK selected by [global.json](../../global.json). They run without Unity or Godot and do not replace validation in engine player builds.

## Numeric and time regressions

- `InputTimePrecisionTests` verifies that hold windows still expire after long simulated uptime with small frame durations, and that direct ingestion updates ignore non-finite time deltas.
- `TimerNumericSafetyTests` covers non-finite constructor times, invalid direct-update deltas, and recovery after mutable timer fields are repaired.
- `NumericSafetyAcceptanceTests` exercises invalid input axes, mutable movement settings, multiplication and integration overflow, stale sequences, and exact hold boundaries through public ECS outcomes.

Invalid-delta tests that call a system directly are separate from `World.Tick`/`FixedTick` validation. The scheduler rejects invalid deltas before normal world execution. These tests establish numeric behavior; they do not measure frame time or engine allocations.

Regression coverage includes cross-queue activation handles, callback-target release after selection/cancellation, input dequeue budgets and control reuse, finite streaming configuration and large-distance overflow, health validation before mutation, and complete timer transition ordering.

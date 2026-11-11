# Bezoro.Events.Tests
Unit tests for `Bezoro.Events` covering event bus behavior, queueing semantics, chaining, cancellation, and core event types.

## Coverage
| Folder                     | Source Mirror                                   | Description                                                                     |
|----------------------------|-------------------------------------------------|---------------------------------------------------------------------------------|
| `Services/EventBus`        | `src/Bezoro.Events/Services/EventBus.cs`        | Publish/subscribe, priority ordering, queueing, disposal, concurrency, chaining |
| `Services/UnityEventBuses` | `src/Bezoro.Events/Services/UnityEventBuses.cs` | Unity loop-specific queue flushing and disposal behavior                        |
| `Types`                    | `src/Bezoro.Events/Types/*`                     | `EventContext<T>` and `SubscriptionHandle` behavior                             |
| `Services/Fixtures`        | Test-only                                       | Shared test event records used across service/type tests                        |

## Run
```bash
dotnet test tests/Bezoro.Events.Tests/Bezoro.Events.Tests.csproj -c Release -f net10.0 --verbosity minimal
```

## Conventions
- Test class naming: `{TypeName}Tests`
- Test method naming: `Method_WhenCondition_ShouldExpectation`
- One behavior per test with explicit Arrange/Act/Assert flow
- Fixtures and shared test-only types live under `Services/Fixtures`

Tests use the .NET 10 SDK selected by [global.json](../../global.json). They run without Unity or Godot and do not replace validation in engine player builds.

## Lifecycle regressions

`EventBusLifecycleSafetyTests` verifies that handles from different buses do not unsubscribe local handlers, and that subscription/enqueue races with disposal leave no retained handlers or queued events. Bounded-flush tests cover budgets, callback-enqueued work, invalid budgets, and nested flush counts. `EventBusAdmissionTests` verifies atomic capacity under concurrent producers, allocation-free full rejection, zero capacity, disposal precedence and bounded default passes with callback reposts.

The suite tests bus bookkeeping and publication semantics. It does not make subscriber-owned state thread-safe or require disposal to wait for callbacks already running.

Queued budgets belong to `IEventBus` and phase groups directly. Regression coverage includes error reporting, reporter failures, borrowed bus lifetime, unique owned-bus disposal, and disposal continuation after failures.

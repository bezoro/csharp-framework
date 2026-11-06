# Bezoro.Events

A typed event bus with priority ordering, cancellable propagation, synchronous publication, and queued dispatch. Targets .NET 10 and .NET Standard 2.1. Unity 6 can consume the precompiled .NET Standard assembly; the library has no dependency on Unity or Godot.

## Publish and subscribe

```csharp
using Bezoro.Events.Services;

using var bus = new EventBus();
var received = 0;
var handle = bus.Subscribe<DamageEvent>(context => received += context.Data.Amount);

bus.Publish(new DamageEvent(25));
bus.Unsubscribe(handle);

public readonly struct DamageEvent
{
    public DamageEvent(int amount) { Amount = amount; }
    public int Amount { get; }
}
```

Events must be structs. Higher-priority handlers run first; equal priorities preserve subscription order. Set `context.Handled = true` to stop propagation through the remaining handlers of that publication:

```csharp
bus.Subscribe<DamageEvent>(context =>
{
    if (shieldIsActive)
        context.Handled = true;
}, priority: 100);
```

`Publish` invokes handlers synchronously on the calling thread and returns the resulting `EventContext<TEvent>`. Nested publications run immediately. Each handler exception is caught so later handlers can continue, unless the context has been marked handled. Pass `reportHandlerError: error => Report(error)` to the `EventBus` constructor to observe caught handler exceptions. The reporter runs on the dispatch thread; reporter exceptions are also caught. With no reporter, failure reporting adds no dispatch work.

## Queue work for a game loop

```csharp
using Bezoro.Events.Abstractions;
using Bezoro.Events.Services;

using IEventBus bus = new EventBus();

// A producer thread can enqueue this immutable value.
bus.Enqueue(new DamageEvent(25));

// Call from the engine's main thread to invoke handlers there.
int dequeued = bus.FlushQueued(maxEvents: 100);
```

`Enqueue` stores an event without invoking handlers. The thread that calls `FlushQueued` executes those handlers. Use one consumer to preserve handler execution order. Concurrent flushes can execute callbacks in parallel even though the queue itself is thread-safe.

`FlushQueued(maxEvents)` returns its own dequeue count, including events with no handlers or failed dispatch. It stops at the budget or an observed empty queue. Callbacks may enqueue more events that consume the remaining budget. Nested flushes have independent budgets and counts, so the budget does not bound elapsed time or all callback work.

Zero leaves a live queue unchanged; negative budgets throw `ArgumentOutOfRangeException` for `maxEvents`. Parameterless `FlushQueued()` drains until it observes an empty queue, including events enqueued by callbacks. Continuous producers or self-enqueueing callbacks can keep that call running. The queue has no capacity limit or backpressure; a flush budget limits consumption, not retained memory.

## Ownership and concurrency

- Subscriptions use snapshots. A handler can subscribe, unsubscribe, or publish during dispatch without corrupting iteration. A removed handler can still run in an already captured snapshot.
- Handles created by `EventBus` have identifiers unique across its instances in the running process. Passing one bus's handle to another returns `false` without removing its subscription. The public handle constructor is not an ownership or security boundary.
- `Dispose()` clears queued events and subscriptions. Registration and enqueue operations synchronize with disposal, preventing those operations from retaining new work afterward. Dispatch, registration, enqueue, and unsubscribe operations on a disposed bus throw `ObjectDisposedException`; counts remain readable and return zero. Disposal itself is idempotent.
- Disposal does not join running publications or cancel callbacks already captured in snapshots. Stop publishers and consumers before disposing engine-owned objects used by handlers.
- Bus bookkeeping is thread-safe. Handler state, event reference fields, and `EventContext` access outside its dispatch are the caller's responsibility.
- Publication allocates an `EventContext`, rents a handler-snapshot array when handlers exist, and returns that array after dispatch. Enqueue allocates one typed dispatch object that owns the event value, without a captured closure or per-event delegate. This is not a zero-allocation event system.

## Unity 6 example

This component owns its bus, subscribes during `Awake`, and flushes on the main thread. The event uses ordinary struct syntax supported by Unity's project compiler:

```csharp
using Bezoro.Events.Services;
using Bezoro.Events.Types;
using UnityEngine;

public sealed class DamageEventLoop : MonoBehaviour
{
    private EventBus _bus;
    private SubscriptionHandle _handle;

    private void Awake()
    {
        _bus = new EventBus();
        _handle = _bus.Subscribe<DamageEvent>(OnDamage);
    }

    // Stop external producers before destroying this component.
    public void QueueDamage(int amount) => _bus.Enqueue(new DamageEvent(amount));

    private void Update() => _bus.FlushQueued(100);

    private void OnDamage(EventContext<DamageEvent> context)
    {
        Debug.Log($"Damage received: {context.Data.Amount}");
    }

    private void OnDestroy()
    {
        if (_bus == null) return;
        _bus.Unsubscribe(_handle);
        _bus.Dispose();
    }
}

public readonly struct DamageEvent
{
    public DamageEvent(int amount) { Amount = amount; }
    public int Amount { get; }
}
```

The same pattern applies to Godot: enqueue from producers and flush in `_Process` or `_PhysicsProcess`. Neither engine's scene APIs should be called from a producer's inline `Publish` unless that producer already runs on the scene thread.

`UnityEventBuses` groups `Update`, `FixedUpdate`, and `LateUpdate` buses. Call `FlushUpdate(maxEvents: 100)` or the equivalent phase method to apply a dequeue budget. Disposal releases every distinct owned bus once, attempts all of them after failures, and then throws an `AggregateException` containing those failures. Repeated disposal is a no-op. `ownsBuses: false` leaves supplied buses usable by their owner. Flush methods on the disposed group throw even when its buses are borrowed.

## API reference

| API | Purpose |
| --- | --- |
| `IEventBus` | Subscribe, unsubscribe, publish, enqueue, bounded or unlimited flush, and counts. |
| `EventBus` | Sealed implementation with optional handler-error reporting. |
| `EventContext<TEvent>` | Publication data and its `Handled` propagation flag. |
| `SubscriptionHandle` | Subscription identifier; `None` is invalid and `IsValid` means `Id > 0`. |
| `IUnityEventBuses`, `UnityEventBuses` | Independent buses for three game-loop phases. |

## Migration

`IBoundedEventBus` has been removed. Depend on `IEventBus` and call `FlushQueued(maxEvents)` directly. Custom bus implementations must implement the bounded overload. Custom `IUnityEventBuses` implementations must also supply the three bounded phase flush methods.

## Build and validation

Use the SDK selected by [global.json](https://github.com/bezoro/csharp-framework/blob/7937b570b52cc850e10572251fb982c3d8d940f0/global.json):

```bash
dotnet build src/Bezoro.Events/Bezoro.Events.csproj -c Release
dotnet test tests/Bezoro.Events.Tests/Bezoro.Events.Tests.csproj -c Release -f net10.0
```

See [the Events tests](https://github.com/bezoro/csharp-framework/blob/7937b570b52cc850e10572251fb982c3d8d940f0/tests/Bezoro.Events.Tests/README.md) for lifecycle and concurrency coverage.


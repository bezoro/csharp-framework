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

using IEventBus bus = new EventBus(maximumQueuedEvents: 1024);

// A producer can handle backpressure explicitly. No handlers run here.
if (!bus.TryEnqueue(new DamageEvent(25)))
{
    // Retry, aggregate or reject according to the application policy.
}

// Call from the engine's main thread to invoke handlers there.
int dequeued = bus.FlushQueued(maxEvents: 100);
```

`Enqueue` stores an event without invoking handlers. The thread that calls `FlushQueued` executes those handlers. Use one consumer to preserve handler execution order. Concurrent flushes can execute callbacks in parallel even though the queue itself is thread-safe.

`FlushQueued(maxEvents)` returns its own dequeue count, including events with no handlers or failed dispatch. It stops at the budget or an observed empty queue. Callbacks may enqueue more events that consume the remaining budget. Nested flushes have independent budgets and counts, so the budget does not bound elapsed time or all callback work.

Zero leaves a live queue unchanged; negative budgets throw `ArgumentOutOfRangeException` for `maxEvents`. Parameterless `FlushQueued()` captures the pending count at entry and processes at most that many events, so callback reposts and continuous producers cannot extend its own dequeue budget. Nested flushes and handler work still have separate costs.

`EventBus(maximumQueuedEvents: ...)` makes queued admission bounded. `TryEnqueue` returns false when full, before allocating a dispatch envelope; `Enqueue` throws `InvalidOperationException` instead. Both throw after disposal. Zero disables queued admission while preserving direct `Publish`. The default constructor uses -1 (unbounded); choose a finite limit for worker-to-engine adapters. `QueuedCount` is a coherent instantaneous pending count, and `MaximumQueuedEvents` exposes the configured limit. Capacity bounds pending event count rather than the memory reachable through event reference fields. Queuing and dequeue synchronize briefly; handlers execute outside that gate.

## Migration

Parameterless `FlushQueued()` now performs a bounded pass using the entry count. Code that intentionally drains callback-posted work in one call can pass an explicit budget, such as `FlushQueued(maxEvents: 100)`, or schedule further passes. `IEventBus` implementations now expose `MaximumQueuedEvents` and `TryEnqueue`.

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

Use the SDK selected by [global.json](https://github.com/bezoro/csharp-framework/blob/0e73014d65f4c74f1842aac64a9be427dd3f0600/global.json):

```bash
dotnet build src/Bezoro.Events/Bezoro.Events.csproj -c Release
dotnet test tests/Bezoro.Events.Tests/Bezoro.Events.Tests.csproj -c Release -f net10.0
```

See [the Events tests](https://github.com/bezoro/csharp-framework/blob/0e73014d65f4c74f1842aac64a9be427dd3f0600/tests/Bezoro.Events.Tests/README.md) for lifecycle and concurrency coverage.


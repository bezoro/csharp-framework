# Bezoro.Core

Engine-independent collections, grids, object pools, value types, results, and guard extensions shared by the framework. The library targets .NET 10 and .NET Standard 2.1. Unity 6 consumes the precompiled .NET Standard DLLs; its project compiler does not need to compile the framework's source syntax.

## Data utilities

[Binary span cursors](https://github.com/bezoro/csharp-framework/blob/23fe934e2d88f2db7662caaaf6bb6c61cac84fbc/src/Bezoro.Core/Binary/README.md) read and write explicit little-endian fields with bounds checks and no storage growth. [Bit sets](https://github.com/bezoro/csharp-framework/blob/23fe934e2d88f2db7662caaaf6bb6c61cac84fbc/src/Bezoro.Core/Collections/README.BitSet.md) store fixed-capacity flags in words; span extensions provide lower/upper bounds and ordered/swap-back removal with reference clearing. [Numeric helpers](https://github.com/bezoro/csharp-framework/blob/23fe934e2d88f2db7662caaaf6bb6c61cac84fbc/src/Bezoro.Core/Numerics/README.md) cover signed chunk division, cyclic indices, interpolation, and elapsed-time damping with explicit finite-input policies.

Use the new operations where their contracts fit the game. Binary readers do not validate application schemas, sorted span searches require caller-maintained ordering, and mutable bit sets require external serialization.

## Object pools

Supply a reset policy when rented objects carry mutable state:

```csharp
using System.Text;
using Bezoro.Core.Types;
using Bezoro.Core.Types.Pool;

var policy = new PoolPolicy<StringBuilder>(
    factory: () => new StringBuilder(),
    reset: builder => { builder.Clear(); return true; });

using var pool = new ObjectPool<StringBuilder>(policy, new PoolOptions(
    InitialCapacity: 16,
    MaxCapacity: 128,
    EnableAsyncWait: true,
    TrackStatistics: true));

using (var handle = pool.RentHandle())
{
    handle.Value.Append("Temporary text");
}

// The handle has returned the cleared builder to the pool.
var statistics = pool.Statistics;
```

`Rent()` reuses an available object or creates one within capacity. `TryRent()` only takes an available object; it does not create one. Exhausted synchronous rents throw `PoolExhaustedException`. Async rents can wait when `EnableAsyncWait` is enabled and capacity is bounded:

```csharp
var item = await pool.RentAsync(TimeSpan.FromSeconds(1), cancellationToken);
if (item != null)
{
    try { item.Append("Work"); }
    finally { pool.Return(item); }
}
```

- The timed overload returns `null` on timeout. Finite timeouts range from zero to `int.MaxValue` milliseconds and are validated before taking an object. `Timeout.InfiniteTimeSpan` waits without a time limit. `RentAsync(CancellationToken)` also waits without a time limit.
- Cancellation throws `OperationCanceledException`. Disposing the pool completes pending waits with `ObjectDisposedException`.
- Returning the same available object again returns `false` without running its reset policy again. Return processing claims ownership before invoking reset, including when accepting a foreign object. Concurrent duplicate returns of the claimed object run reset at most once. A stale raw reference is still invalid after the object is rented again; callers must not return or mutate it.
- A foreign object can be accepted while the live pool has free capacity. A full pool discards the foreign object and keeps ownership of its existing rentals. Returning an unknown object after disposal returns `false` without discarding it; its owner remains responsible for cleanup.
- A rejected or throwing reset, or throwing rent validation, removes and discards the affected owned object. Cleanup releases capacity. Exceptions from reset or validation remain visible to the caller.
- Rent validation and `OnRent` run before the rental becomes returnable. Returning that same item from either callback returns false, so it cannot become available to a second consumer while acquisition is still in progress.
- Failed prewarming releases objects already created. Clearing available objects attempts cleanup of every object even when one cleanup callback throws, then rethrows the first failure.
- `Clear()` disposes available `IDisposable` values directly. `ClearWithPolicyDiscard()` uses the policy's discard callback. `Dispose()` clears available objects; outstanding rentals are discarded when returned afterward. Return rentals before disposing their pool when possible.
- Copies of `PooledObjectHandle<T>` share disposal state and return their value once. `RentHandle()` allocates that shared state. Raw `Rent()`/`Return()` avoids the handle allocation but requires explicit ownership discipline.

Pool bookkeeping is thread-safe. Available references use reusable stack storage under the lifecycle lock. Storage can grow when the number of available objects reaches a new peak; warmed reuse does not allocate a node per return. Timed rents that complete immediately also allocate no timer object. Actual async waits allocate cancellation and task state. Rented objects are not made thread-safe by the pool, and policy callbacks may run concurrently for different objects. A single rental should have one owner. Statistics are concurrent observations rather than an atomic snapshot of every counter.

`new PoolOptions()` uses `PoolOptions.Default`, including rent validation and async waiting. An omitted or uninitialized options value also selects those defaults when passed to the pool. `MaxCapacity = -1` is unbounded; zero permits no owned objects. Negative initial capacity, maximum capacity below -1, and initial capacity above a bounded maximum are rejected before calling the factory. A failed factory releases its reserved capacity and wakes waiting renters. A throwing policy discard callback still disposes its `IDisposable` value; if both fail, their exceptions are aggregated.

Call `TrimExcess(...)` to trim explicitly, and select a timed rent when a deadline is needed. Statistics counters require `TrackStatistics = true`.

## Collections

```csharp
using Bezoro.Core.Types;

var entityIds = new SwapbackArray<int> { 10, 20, 30 };
if (entityIds.TryGetIndex(20, out uint index))
    entityIds.TryRemoveAt(index);

entityIds.ClearRetainingCapacity(); // Reuse the backing allocation.
entityIds.Clear();                  // Clear and shrink to minimum capacity.
```

`SwapbackArray<T>` removes an item by moving the last item into its slot. Indexed removal is O(1) before any optional shrink; searching by value is O(n), and shrinking copies retained values. Set `ShrinkThresholdPercent = Percent.Zero` to defer shrinking until the collection is empty, or reuse capacity with `ClearRetainingCapacity()` between workloads.

`ReplaceAt` and the indexer require `index < Count`. `TryGetIndex` returns `false` with index zero when absent. Adding the collection directly to itself appends its current contents once. An enumerable that reads the collection indirectly, such as a LINQ query over it, is invalidated when appending starts and throws instead of growing indefinitely. A failed enumeration can leave successfully appended items in the destination and invalidates existing enumerators.

These collections require external synchronization for concurrent mutation and access. Spans returned by `AsSpan()` or `AsMutableSpanUnsafe()` borrow storage and must not survive operations that resize it. Mutating the unsafe span bypasses enumeration version tracking.

## Grids and borrowed views

For fixed-capacity gameplay storage, see [generational slot maps and ring buffers](https://github.com/bezoro/csharp-framework/blob/23fe934e2d88f2db7662caaaf6bb6c61cac84fbc/src/Bezoro.Core/Collections/README.md). For replayable procedural generation, shuffling, and weighted selection, see [deterministic random streams](https://github.com/bezoro/csharp-framework/blob/23fe934e2d88f2db7662caaaf6bb6c61cac84fbc/src/Bezoro.Core/Random/README.md). Their capacities and state ownership are explicit; they do not introduce global random state or synchronize mutable game data implicitly.

```csharp
using Bezoro.Core.Types;

using var grid = new Grid2D<int>(width: 8, height: 8, usePooling: true);
grid[2, 3] = 5;
var row = grid.GetRow(3);
var view = grid.AsGridSpan();
```

Grids use row-major storage. Dimensions must be positive, and dimension multiplication is checked for integer overflow. `GridSpan2D<T>` and `ReadOnlyGridSpan2D<T>` require enough backing elements for the complete grid. Indexers validate each coordinate independently, so an invalid column cannot alias another row.

A newly rented grid initializes its logical cells to `default(T)`. The default-value constructor fills those cells with the supplied value. Pooling reuses the backing array; it does not remove the cost of initializing or clearing elements.

`Dispose()` is idempotent and returns a pooled backing array once. Subsequent storage access through the owning grid throws `ObjectDisposedException`. Existing spans, rows, and refs cannot enforce that lifetime: stop using all borrowed views before disposal. Coordinate queries such as `IsInBounds` only inspect dimensions and do not establish that storage is alive. Synchronize grid access and disposal externally.

## Results and guards

```csharp
using Bezoro.Core.Types;

var result = ParseNumber("42");
var (ok, value) = Try.TryGet(() => int.Parse("42"));

static Result<int> ParseNumber(string input) =>
    int.TryParse(input, out var value)
        ? ResultFactory.Succeeded(value)
        : ResultFactory.Failed<int>(new ParseFailure(input));

sealed record ParseFailure(string Input) : IFailureReason;
```

Prefer direct guards or focused extensions such as `ThrowIfNull` and `ThrowIfEmpty` in new code. The obsolete `ValidationHelper.ThrowIfObjectIsNull` now follows its documented contract: a non-null value passes, and a null value throws with the supplied parameter name and details.

Integer `Abs` throws for `int.MinValue`, whose positive magnitude cannot fit in an `int`. `RoundToNearest` requires a positive multiple, rounds midpoints away from zero, and throws if the rounded result cannot fit. Both handle negative inputs without wrapping.

`FloatExtensions.Map` accepts distinct finite source endpoints, including reversed or very small ranges. Intermediate calculations use double precision so finite float endpoints do not overflow during subtraction. It does not clamp extrapolated output; a result outside the float range becomes infinity. Approximate comparers require finite, nonnegative tolerances. They compare opposite finite extremes without mistaking two overflowing intermediate values for equal distances.

`Percent.FromTotals(first, second)` combines two current/maximum pairs with widened integer sums and no temporary array. This overload supports aggregate properties such as health plus shield totals without a `params` allocation on every read.

## Arrays and asynchronous processing

`ProcessAsync` visits non-null cells of a two-dimensional array in row-major order. Its callback receives the value, column, row, and cancellation token. It processes one callback at a time and adds no forced scheduler yield. Callbacks that complete synchronously allow the operation to complete synchronously. Cancellation, including cancellation requested by the final callback, propagates as `OperationCanceledException`; callback failures also propagate. It does not marshal callbacks to an engine or UI thread.

Array searches tolerate null storage slots. Array equality uses typed equality comparers without boxing value elements. Generic `HasAny` reads a collection's count when available, including `IReadOnlyCollection<T>`, and otherwise disposes the enumerator after its first move. String predicates inspect whitespace without allocating trimmed copies; the legacy `IsEmpty` and `IsNullOrEmpty` string extensions also regard whitespace as empty.

## Colors and coordinates

`Color.TryParse` accepts hexadecimal RGB/RGBA and exact `rgb(...)` or `rgba(...)` functions. CSS functions need exactly three or four components respectively and cannot have trailing data. Integer CSS channels clamp to 0..255 and finite alpha clamps to 0..1. Hex digits cannot contain embedded whitespace. Floating color constructors reject non-finite channels.

`Color.ToString("CSS", null)` returns CSS; `RGB`, `RGBA`, and `ARGB` select hexadecimal layouts. `TryFormat` accepts the same case-insensitive formats without allocating temporary strings and leaves an undersized destination unchanged. Packed 32-bit conversion round-trips every channel under normal and checked compilation. `UIntVector2` rejects the float value 2^32 before conversion; the largest valid float coordinate is 4294967040.

## Singleton ownership and code generation

`Singleton<T>` creates its default instance once under a per-type gate. Configuration, creation, reset, and override changes share that gate. Reading an existing instance uses volatile reads. A factory may construct the type only on its calling thread; another thread cannot bypass the constructor guard while a factory runs. A failed default creation remains failed until reset or recreation.

`Initialize` owns one eager override. `Override` owns a distinct instance until its returned scope is disposed. Close nested scopes in reverse creation order; an out-of-order close throws before changing state. `ResetAndDispose` and `ConfigureFactoryAndRecreate` dispose every owned default, initialized, and scoped instance, then invalidate outstanding scopes. Later disposal of an invalidated scope does nothing. `Reset` forgets those instances without disposing them. Disposal exceptions remain suppressed by these lifecycle operations. A factory or disposal callback cannot recursively change the same singleton's lifecycle.

Returned instances are borrowed references. Finish using them before another thread resets, recreates, or closes their owning override. The singleton gate does not protect application operations on those references.

Default creation uses a parameterless constructor through reflection. Supply a direct factory in trimmed or AOT applications when the application's linker policy would otherwise remove that constructor.

`CodeWriter.BeginScope` returns an idempotent scope. Close nested scopes in reverse order; closing a nonexistent scope throws. `Write` followed by `WriteLine` continues the current line without inserting another indentation prefix. `CSharpCodeBuilder` rejects namespace boundaries inside an open class. Generated using directives sort ordinally, independent of the current culture.

## Migration from earlier utility APIs

- Replace the two-dimensional `array.Process(...)` call with `array.ProcessAsync(...)`. Catch cancellation when the host intends to stop processing quietly.
- Remove `PoolOptions.ShrinkThreshold` and `PoolOptions.AsyncWaitTimeout`; neither option previously affected execution. Call trimming and timed rent methods explicitly.
- Use `MaxCapacity = -1` for an unbounded pool. Zero now means zero capacity. Set `ValidateOnRent = false` explicitly when validation should be disabled.
- Close singleton override scopes in reverse order, and return a fresh instance from each override factory.
- Supply finite color channels and tolerances. Malformed CSS previously accepted by the permissive parser now returns false.

## Other APIs

| API | Purpose |
| --- | --- |
| `Percent`, `Color`, `UIntVector2`, `UIntRange` | Common value types and their formatting helpers. |
| `Singleton<T>` | Explicit configure, recreate, reset, and dispose operations for a shared instance. |
| `SingleItemEnumerable<T>` | Enumerates one value. |
| String formatting extensions | Rich-text formatting with `Color(string)` and `Color(Color)`. |
| `CodeWriter`, `CSharpCodeBuilder` | Builds generated C# source. |

Format enum values with `ToString()` and then the string extensions. `ColorHex`, parameterless named-color methods, and enum styling wrappers remain obsolete compatibility APIs.

## Build and validation

Use the SDK selected by [global.json](https://github.com/bezoro/csharp-framework/blob/23fe934e2d88f2db7662caaaf6bb6c61cac84fbc/global.json):

```bash
dotnet build src/Bezoro.Core/Bezoro.Core.csproj -c Release
dotnet test tests/Bezoro.Core.Tests/Bezoro.Core.Tests.csproj -c Release -f net10.0
```

The first command builds both runtime targets. See [the Core tests](https://github.com/bezoro/csharp-framework/blob/23fe934e2d88f2db7662caaaf6bb6c61cac84fbc/tests/Bezoro.Core.Tests/README.md) for covered contracts.


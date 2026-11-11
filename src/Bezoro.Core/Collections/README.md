# Bounded game collections

`SlotMap<T>` and `RingBuffer<T>` allocate their storage once. Neither grows or creates tasks during normal operations. They are independent of ECS, Unity, and Godot.

## Stable handles with dense values

Use a slot map when game code needs to retain identifiers while storage is compacted, such as projectile registries, active interactions, and local simulation objects:

```csharp
using Bezoro.Core.Collections;

var projectiles = new SlotMap<ProjectileState>(capacity: 4096);
SlotHandle handle = projectiles.Add(new ProjectileState());

if (projectiles.TryGet(handle, out var state))
{
    state = Integrate(state, deltaTime);
    projectiles[handle] = state;
}

projectiles.Remove(handle);
// TryGet(handle, ...) now returns false, including after that slot is reused.
```

Capacity can be zero, in which case every add is rejected. `TryAdd` returns false when no slot is available; `Add` throws. `TryGet`, `Contains`, and removal reject default, stale, and foreign-map handles. Handle identifiers apply only to their owning map in the current process. They are not save-file identifiers, network entity IDs, or stable hash values across runs. Objects from two maps cannot accidentally share a live handle, including maps with different value types.

Values occupy a dense array. Removing an entry moves the last entry into its position in O(1). The moved entry retains its handle, but its dense index changes. `GetHandleAt` connects the current `Values` view to stable handles. Insertion and removal order determine dense order; insertion order is not preserved after removal. Replacing a value through the handle indexer preserves identity.

`Values` is a borrowed `ReadOnlySpan<T>`. Finish reading it before any mutation. Concrete `foreach` over the map uses a struct enumerator and checks for mutations. Enumerating through `IEnumerable<T>` boxes the enumerator. Neither mechanism makes concurrent mutations safe.

Removal and `Clear` release stored references without disposing values. The caller retains disposal responsibility. `Clear` invalidates every live handle and restores reusable slots in O(Count). Each slot has a 32-bit generation. Once that generation reaches its maximum and is removed, the slot is permanently retired instead of wrapping and resurrecting an old handle. `AvailableCapacity` accounts for retirement. This is a lifetime capacity reduction after roughly 4.29 billion reuse cycles of one slot, not a reason to reuse stale handles.

For capacity N, backing storage comprises N values, N dense-to-slot integers, and N slot records of two integers plus a generation. This is approximately N × (value size + 16) bytes, excluding array headers, alignment, the map object, and any referenced objects. A `SlotHandle` contains one 64-bit owner ID, a 32-bit slot index, and a 32-bit generation. No boxed values or per-entry wrapper objects are required.

## Bounded FIFO history

Use a ring buffer for a recent-input history, replay window, telemetry samples, or a queue with an explicit producer backlog limit:

```csharp
var recentInputs = new RingBuffer<InputFrame>(capacity: 120);
var commands = new RingBuffer<GameCommand>(capacity: 256);

// Keep the newest 120 inputs. Inspect or release an evicted item when needed.
bool evicted = recentInputs.EnqueueOverwrite(frame, out var oldest);

// Alternatively reject a new item when preserving every queued item matters.
if (!commands.TryEnqueue(command)) HandleBackpressure();

while (commands.TryDequeue(out var next)) Apply(next);
```

The capacity must be positive. `TryEnqueue` returns false without mutation when full, `Enqueue` throws when full, and `EnqueueOverwrite` explicitly evicts the oldest item. Its return value tells whether eviction occurred; the output is default otherwise. Values are indexed and enumerated oldest first. `TryPeek` does not remove a value.

`CopyTo(Span<T>)` copies the logical FIFO sequence, including wrapped storage, into caller-owned memory and returns the number copied. It validates destination capacity before writing and holds the operation gate for the complete copy. A capacity-sized destination avoids racing a separate `Count` read. The snapshot is shallow: referenced objects retain their own ownership requirements. Dequeue, overwrite, and clear release superseded references; none of these operations disposes values. Storage is one T[N] array plus the buffer object, its gate and counters. Enqueue, dequeue, peek, indexed reads, and overwrite are O(1); copying, enumeration, and clear are O(Count).

## Ownership and profiling

Slot maps require an owning execution or external serialization for all access, including the full lifetime of borrowed `Values` spans. Map owner-ID assignment is thread-safe; this does not make the map itself concurrent. Ring-buffer operations synchronize per instance and support concurrent producers and consumers. Compound sequences of operations remain the caller's responsibility; enumeration rejects intervening mutation rather than retaining a lock across user code. Independent owned kernels can run on separate simulation workers. Unity scene objects and Godot nodes still follow their engine's thread rules.

The allocation regression warms complete operations and checks fixed samples for a zero-allocation batch. It covers successful slot add/read/remove, ring enqueue/dequeue, and concrete enumeration. The checked-in BenchmarkDotNet workloads separate construction from steady-state operations:

```sh
dotnet run -c Release --project benchmarks/Bezoro.Core.Benchmarks -- --filter '*GameCollectionsBenchmarks*'
```

Measurements on .NET do not establish Unity Mono, IL2CPP, mobile, WebGL, or Godot export frame budgets. Profile the intended target with representative value types and capacities.

# Concurrency and engine integration

The framework supports concurrent services, immutable shared data and bounded parallel execution without depending on Unity, Godot or a particular engine thread. Thread safety does not mean creating a task for every operation. Most shared operations remain synchronous and allocate no managed memory after their storage has warmed.

Engine adapters own scene-thread dispatch. They can submit data work to workers and consume the results on the engine thread; they must not make a data-only simulation single-threaded merely because rendering or scene mutation has thread affinity.

## Choose the ownership boundary

| Boundary | Contract |
| --- | --- |
| Immutable definitions, catalogs, maps, positions and readonly results | Share across workers after construction. Referenced application objects are not made immutable by a readonly wrapper. |
| Shared services and mutable accounts | Public operations synchronize their own state. Use coherent copy/snapshot operations when several fields must describe the same instant. Separate calls are not a transaction. |
| Execution workspaces and borrowed storage | Give each active execution exclusive ownership. Different owners may run on different workers; ownership does not imply the engine's main thread. |
| Engine objects and application callbacks | Follow the callback's own threading and lifetime contract. A thread-safe container cannot make its contents or callbacks safe. |

Directly borrowed spans and refs cannot be protected by locking their getter: the borrow outlives that lock. `SlotMap`, `BitSet`, `SwapbackArray`, span cursors, grids, random state variables and builder objects remain explicitly owned storage. Coordinate the entire borrow, mutation and disposal interval. Separate bit indices can share a storage word; they are not automatically independent write partitions. A pooled grid must not be disposed while another worker holds one of its rows or refs.

## Module contracts

| Modules | Concurrent use |
| --- | --- |
| Core pools and ring buffers | Pool bookkeeping and individual ring operations are synchronized. Policy callbacks execute on the renting/returning/clearing caller's thread. Ring `CopyTo` returns a coherent FIFO snapshot count; enumeration rejects intervening mutation. |
| Conditions and flat state machines | Compiled definitions are immutable. Every evaluation owns its context, evaluator and scratch until it returns. |
| Behavior trees, statecharts and composed behaviors | Independent owners execute in parallel. Atomic admission rejects overlapping updates on the same owner before effects; token validation and transition effects share that admission. Contexts and direct state inspection remain owner-bound. |
| ECS | Declared job access and bounded world groups provide parallel execution. Worlds retain exclusive administrative/storage ownership. Command streams accept concurrent recording; cross-producer ordering follows admission order. Separate streams are preferable when deterministic producer ordering matters. Cursor callbacks/jobs pin their generation against disposal. |
| Simulation | Tick scheduling, cancellation, advancement and attribute-cache operations synchronize per instance. Read several related fields through one copied result when coherence matters. |
| Spatial and Navigation | Spatial queries may run in parallel with separate destinations; mutations are exclusive and waiting writers have priority. Navigation shares immutable maps and rejects overlapping use of one search workspace; give each active worker its own workspace. |
| Events and GameSystems | Producers may enqueue concurrently. Select an explicit queue capacity and use `TryEnqueue` for backpressure. Callback dispatch happens on the flushing thread, outside bookkeeping locks. Use one consumer when callback order matters. |
| Inventory, Commerce, Profiles and Progression | Per-instance operations synchronize. Inventory transfers acquire both owners in a consistent order. Snapshots copy owned values; cross-module rewards and durable backend transactions remain host responsibilities. |
| Logging and Typing | Logger readers capture immutable configuration; category changes publish copies. Typing metrics and word consumption/mutation synchronize, with coherent readonly metrics snapshots. Subscriber state remains application-owned. |
| Chess | Protocol local commands and snapshots synchronize; engine work uses captured histories and rejects stale results. Facade commands serialize and concurrent asynchronous disposals join the same teardown. Study editing cursors and authoring builders remain explicit owners; immutable positions may be shared. |
| Persistence | Operations own their streams and synchronize same-process file commits by normalized path. Payload/read buffers are exclusively borrowed until each asynchronous call completes. |

Synchronization protects complete operations, not arbitrary sequences of calls. For example, checking a queue count and then enqueuing can race with another producer; use `TryEnqueue`. An inventory snapshot is coherent, but separately reading a wallet and quest state does not create an atomic reward transaction across those modules.

## Workers and engine adapters

Use the existing bounded ECS job/world scheduling or the host's worker pool for substantial data work. A worker can publish an immutable result through a bounded `EventBus` or `RingBuffer`. The Unity/Godot adapter flushes a finite amount of work at its chosen update phase and performs scene operations there. Queue capacity bounds retained work; a flush budget bounds dequeues, not callback duration or nested work.

Each API documents its callback thread. Pool policies do not marshal calls, and event publication does not silently select an engine context. A factory that instantiates a prefab or a discard callback that frees a scene node must be invoked through the appropriate adapter boundary. Pure C# objects can use the same core pool directly on workers.

Default event and activation dispatch passes process a finite batch captured at entry. New self-posted work remains for a later pass. For applications needing a different policy, explicit dequeue budgets remain available. There are no implicit per-node tasks, global main-thread dispatcher or hidden frame loop.

## Lifetime and failure

Callbacks run outside internal bookkeeping locks. Explicit synchronization primitives such as `TicketLock.ExecuteOrdered` intentionally execute the caller's critical section under synchronization and require the caller to avoid circular waits.

Singleton lifecycle changes reserve ownership before invoking factories/disposal callbacks outside the monitor. Readers join one pending default creation; conflicting lifecycle changes fail before mutation. Circular factory dependencies across threads are unsupported. A borrowed singleton value or pooled rental is not a lifetime pin: finish using it before its owner resets, returns or disposes it.

Pool teardown always follows its discard policy, including non-`IDisposable` native handles. Clearing detaches a finite available batch before callbacks, so reentrant returns cannot extend that clear indefinitely. Outstanding rentals follow the same discard policy when returned after disposal.

The training progress recorder bounds accepted pending records, including in-flight writes. A rejected capture produces an immutable ordered fact and retained diagnostics; successful draining does not erase that rejection. The host must retain/recover rejected facts in sequence, pause admission if necessary, then acknowledge the observed rejection generation. Queue limits do not justify silently losing a completed match.

Synchronous observer disposal stops new admission but cannot cancel arbitrary callback code already admitted. Await asynchronous teardown where the API owns tasks or durable writes, and stop publishers before destroying engine objects referenced by their handlers.

## Migration

- Replace `SlotInventory.Slots.CopyTo(destination)` with `CopyTo(destination)`, and `Slots.ToArray()` with `CreateSnapshot()`. The live borrowed slot view was removed.
- `RingBuffer.CopyTo` now returns the copied count. A capacity-sized destination handles concurrent growth without a count-then-allocation race. Copies are shallow; referenced values retain their own ownership requirements.
- Use `ObjectPool.Clear()` for policy-based cleanup. `Clear(bool)` and `ClearWithPolicyDiscard()` were removed; custom `IPool` implementations implement `Clear()` directly.
- Depend on `ISet<LogCategory>` for `LoggerSettings.MutedCategories`; it no longer exposes a mutable `HashSet`.
- Custom `IEventBus` implementations provide bounded-admission members. Parameterless `FlushQueued()` processes the entry batch; use explicit budgets for deliberate callback-produced work.
- Low-level chess events observe committed state and execute outside its state gate. Subscriber failures do not roll back or fail the completed command, and batch receipts exclude subscriber-spawned commands. Clock samples are shared within a logical operation; injected clocks should describe time rather than depend on invocation counts.
- Overlapping singleton lifecycle changes and execution-owner operations now fail before mutation rather than relying on an external lock or racing. Default singleton reads still join one creation.

## Verification limits

Deterministic overlap/callback tests, contention invariants, differential models and warmed allocation checks validate the documented managed contracts. Release dual-target builds and older-compiler consumers check the Unity-facing assembly surface. These checks do not establish Unity Mono, IL2CPP, Godot export or hardware frame-time budgets; measure those in representative engine scenes before making throughput claims.

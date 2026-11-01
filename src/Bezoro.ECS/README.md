# Bezoro.ECS

`World` owns component storage, resources, compiled queries, and system execution. Runtime assemblies target .NET 10 and .NET Standard 2.1. The source generators target .NET Standard 2.0.

## Start with a world and a query

```csharp
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;

using var world = new World(new WorldConfig
{
    EntityCapacity = 1024,
    QueryResultCapacity = 1024,
    MaxDegreeOfParallelism = 1
});

var player = world.Spawn(new Position { X = 1, Y = 2 });
world.Query<Positions>().ForEach<Position>(
    (Entity entity, ref Position position) => position.X += 10);

ref readonly var current = ref world.Read<Position>(player);

[Query, With(typeof(Position))]
readonly partial struct Positions;

struct Position { public float X; public float Y; }
```

A world has one owning thread. Its scheduler and explicit parallel jobs may distribute declared work to workers. Concurrent external mutation of the same world is unsupported. Independent worlds can run concurrently through `WorldGroup`.

## Read and write deliberately

- `Read<T>(entity)` returns a readonly component reference. `TryRead<T>(entity, out value)` returns a copy.
- `Write<T>(entity)` returns a mutable reference and records a potential write for change tracking. `TryWrite<T>` returns the same kind of borrowed reference through `ComponentRef<T>`.
- `GetAccessor<T>()` creates a reusable unmanaged accessor with `Has`, `TryRead`, and `Write` operations.
- `Has`, `TryRead`, `TryWrite`, and removal of an absent component do not register the missing type or consume component capacity.
- `Set`, `Add`, and `Replace` currently all insert or overwrite a component. `Add<T>(entity)` uses its default value. `Remove<T>` removes it when present.

Component references and spans are borrowed. Stop using them before an entity changes archetype, a component is removed, the world resets, or the world is disposed. A readonly reference does not make objects referenced by a component immutable. Entity handles belong to the world that produced them; IDs are not global identities across worlds.

## Record structural changes during iteration

A query borrows live component storage. While an iteration or cursor is active, the world rejects entity creation/destruction, archetype changes, playback, reset, clear, snapshot operations, and disposal. Overwriting a component that already exists is allowed. Record structural work and apply it after iteration:

```csharp
using var commands = world.CreateCommandStream();
world.Query<Positions>().ForEach<Position>((Entity entity, ref Position position) =>
{
    if (position.X < 0) commands.Destroy(entity);
});
world.Playback(commands);
```

`CommandStream.Set<T>` accepts any struct, including structs containing references. One stream belongs to one world and one recorder. Different systems receive independent streams on first access to `context.CommandStream`. A scheduler-provided context is valid only during its `Update` call. Access after that call rejects use rather than allocating a late stream. Explicitly constructed contexts with an external stream retain that stream's own lifetime. A stream rents its command buffer only on the first recorded command. Playback follows recording order and resets the stream even when a command fails. Already applied commands remain applied; playback is not a transaction.

`CreateEntity` returns a temporary handle for commands in that stream's current recording. It differs from `Entity.None` and `Entity.Wildcard`, and cannot be reused across streams or after playback/reset. Temporary identities are allocated only for recordings that create entities. The process rejects creation after all 4,294,967,295 generation tokens have been used; it never recycles a token and aliases an earlier handle.

## Choose query traversal by the work you need

Declare generated queries with `[Query]`, `[With]`, `[Without]`, `[AnyOf]`, `[Optional]`, `[Changed]`, and `[Added]`. Manual specifications implement `ICompiledQuerySpec.Build(ref QueryBuilder)`. Plans are cached by specification type and built from `default(TSpec)`; runtime values on specification instances are not accepted. `All`, `None`, and `Any` remain alternative filter spellings. `Related<TRelation>(target)` and `Related<TRelation>()` select relationships to one target or any target.

```csharp
var positions = world.Query<Positions>();
positions.ForEachRead<Position>((Entity entity, in Position position) => Observe(position));
positions.Run(new MoveJob(0.016f));
```

Typed delegates support all struct components. Struct jobs and the low-level cursor hot paths currently require unmanaged components. Job interfaces support one writable component and up to three readonly components, with an optional entity argument. Generated extensions infer the job's component types for `Run(job)` and `RunParallel(job, degreeOfParallelism)`.

For a bounded window, compile once and index the cursor:

```csharp
var handle = world.Compile<Positions>();
using var cursor = world.Execute(handle);
cursor.MoveNext();
for (int index = 0; index < Math.Min(cursor.Count, 64); index++)
{
    Entity entity = cursor.GetEntity(index);
    ref readonly Position position = ref cursor.Read<Position>(index);
    Observe(entity, position);
}
```

`Count` and `GetEntity(index)` do not materialize the complete entity array. `Current` materializes it on first access. Index lookup reuses the current chunk range or searches the range table. Constructing an execution still visits matching archetypes and chunks; `Changed`/`Added` filtering may examine every candidate entity. A window bounds the caller's component work, not the cost of constructing the result set.

A cursor has one batch. The first `MoveNext` returns true, including an empty result; later calls return false. Dispose every execution. Cursor copies share a lifetime: disposing one invalidates all copies, and an old copy cannot dispose a later execution that reuses the lease. Disposing any copy while its own callback/job is running throws before releasing storage. Default handles and default/disposed cursors reject use with explicit lifecycle exceptions.

`Changed<T>` and `Added<T>` advance their observation window per compiled specification. Handles/views for the same specification in one world share that window. Mutable access records a potential write; it cannot detect assignments through a reference retained beyond its original borrow. Change versions use 64 bits. Compiling the first changed/added query enables potential-write bookkeeping for mutable references; that bookkeeping may synchronize across workers. Explicit add/set operations retain versions for later queries. Query diagnostics observe matching counts without advancing the window.

Empty typed delegate queries invoke no callback and do not register callback-only types. A matching archetype missing a requested callback component throws before that chunk's first callback. Earlier compatible chunks may already have completed.

## Schedule only known independent work in parallel

Systems run by `SystemLoopPhase` and `Stage`. Updates are rejected during registration/`OnCreate` and disposal/`OnDestroy`, before any system runs. Registration honors the system's stage unless an explicit override is supplied. `Tick`, `FixedTick`, and `LateTick` execute their respective phase. Use `[Before]` and `[After]` within the same phase/stage. Declared ordering takes precedence over registration order; remaining component/resource conflicts retain deterministic registration order.

The generator infers direct `Read`/`Write` access, cursor/accessor operations, delegate traversal, and jobs from the effective `ISystem.Update` implementation. It supports explicit interface implementations, inherited source methods, and closed generic base types. Inference complements explicit declarations; it never authorizes parallel scheduling by itself. It does not inspect arbitrary helper method bodies or prove the absence of other effects. Direct structural calls are always exclusive. Declare the complete access contract for any system intended to run in parallel, including access through helpers and DLL consumers without generators:

```csharp
[Reads(typeof(Velocity)), Writes(typeof(Position))]
sealed class IntegrateSystem : ISystem
{
    public void Update(in SystemContext context) { /* declared component work */ }
}

[NoWorldAccess]
sealed class TimerSystem : ISystem
{
    private float elapsed;
    public void Update(in SystemContext context) => elapsed += context.DeltaTime;
}
```

A system without explicit `[Reads]`, `[Writes]`, resource-access attributes, or `[NoWorldAccess]` runs exclusively, even when the generator inferred its component types. `[NoWorldAccess]` opts in to parallel scheduling for updates that use their own data and context values; recording commands in their private stream is allowed. `[Exclusive]` always requires exclusive execution. Code using shared state outside the world must supply its own accurate declarations or use `[Exclusive]`.

Run conditions execute before the system. System sets support enable/disable and set conditions. Fixed intervals cap accumulated catch-up at three intervals and execute at most once per phase call. Each batch flushes its command streams in schedule order after every started worker has joined. A failure disposes all created streams, waits for accepted workers, and propagates the exception. Completed component writes are not rolled back.

Parallel entity jobs partition chunk ranges. Each range receives a job copy, so job fields are not a result accumulator. Referenced objects in a job remain shared and require suitable synchronization. Nested parallel scheduling reuses the active worker. Unity/Godot objects requiring the engine's main thread must stay out of worker code.

`WorldGroup` copies a fixed membership list and does not own its worlds. Set each world's parallelism to one when parallelizing primarily across worlds. Cancellation stops work that has not started and joins started updates before returning.

## Resource ownership

`SetResource`/`ReplaceResource` transfer ownership to the world when registration succeeds. Replacing/removing a disposable resource disposes the previous value. Assigning the same reference keeps it alive. If previous-value disposal throws, the replacement has not been accepted and remains caller-owned. `Clear` and `Dispose` detach ownership and attempt every disposal even after a failure.

`GetOrCreateResource(factory)` invokes the factory only when absent and rejects null. Factories and disposal callbacks may read existing resources, but cannot mutate the resource store or reset/clear/dispose the world reentrantly. A failed factory leaves no registration. Restored struct resources materialize one stable typed box, including concurrent first reads.

Resource references are borrowed until replacement/removal/clear/disposal. Use `ReplaceResource` for ownership changes; replacing a disposable object by assignment through `WriteResource` bypasses disposal bookkeeping. Readonly resource access does not freeze a reference object's contents.

## Capacities and reset

`WorldConfig` sets entity, component-type, command, payload-per-type, and query-result limits. Diagnostics expose usage, high-watermarks, and overflow attempts. `FailFast` throws on overflow; `DropNewest` drops ordinary commands or truncates query results. Entity creation always throws when no handle can be returned. Payload overflow counts toward command-stream diagnostics in both policies.

`Reset` removes entities and change tracking. `Clear` also disposes resources. Both retain registered component IDs, compiled plans, allocated capacity, and diagnostics.

Relationships use a marker ID for each relation-type/target pair and share `ComponentTypeCapacity` with components. Destroying a target removes incoming edges but does not reclaim its marker IDs. Reset/clear also retain those reserved IDs. Target churn can therefore exhaust the catalog even with few live entities. Exhaustion rejects a new relation before changing its edge. Recreate the world to restart the catalog; this storage model does not support unlimited target churn.

## Snapshot transport and ownership

Applications choose their serializer and implement `IWorldSnapshotWriter`/`IWorldSnapshotReader`. Capture produces `WorldSnapshot` records. Restore validates the payload and explicit allow-lists before clearing existing state. Types are denied by default; `AllowAll...` options are for trusted input.

`WorldSnapshot` is a mutable transport DTO, not a deep immutable copy. Its arrays belong to the payload, and captured reference objects remain shared with their source components/resources. A serializer/reader must materialize independent resource instances before restoring into another owner. Restore transfers ownership of accepted resources to the destination world. Do not restore the same disposable instance into multiple worlds or back into a world that will dispose that instance during clear.

## Migration from compatibility APIs

The obsolete compatibility layer has been removed:

| Removed API | Use |
| --- | --- |
| `WorldOptions`, `World(WorldOptions)` | `WorldConfig`, `World(WorldConfig)` |
| `CommandBuffer`, `BeginCommands`, `CreateCommandBuffer`, `SystemContext.Commands` | `CommandStream`, `CreateCommandStream`, `SystemContext.CommandStream` |
| `World.Get`, `GetResource`, `TryGetManaged` | `Read`/`Write`, `ReadResource`/`WriteResource`, `TryRead` |
| Generic `World.Run*` instance forwarders | `QueryView.Run`, `RunEntity`, `RunParallel`, `RunParallelEntity` |
| `QueryCursor.Get`, `ComponentAccessor.Get` | `Write` |
| `ComponentAccessor.TryGet` | `TryRead` |
| `CommandStream.SetManaged` | `CommandStream.Set` |

Generated `world.Run(handle, job)` extensions still infer concrete job types. Empty `OnAdd`/`OnRemove` marker and observer declarations were removed because no runtime subscription API consumed them.

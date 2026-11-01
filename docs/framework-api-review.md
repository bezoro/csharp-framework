# Framework API and runtime review

This review starts at commit [23d613a9](https://github.com/bezoro/framework/commit/23d613a9), covering all 590 tracked C# files in the 18 runtime libraries and the source-generator project. Separate implementation reviews covered each area. FSMs and behavior trees also received an independent consumer, lifecycle and allocation review; Core and ECS changes received independent adversarial checks.

| Area | C# files at the baseline | Review focus |
| --- | ---: | --- |
| Conditions, StateMachines, Statecharts, BehaviorTrees, Behaviors | 39 | Declarative construction, occurrence identity, budgets, interruption and absence of work |
| Core, Simulation, Navigation, Persistence, Inventory, Spatial | 122 | Boundaries, checked arithmetic, allocation, pooling and ownership |
| Chess protocol and facade | 175 | Local play, explicit analysis, immutable snapshots, parsing and process teardown |
| Events, GameSystems, Logging, TypingSystem | 108 | Public contracts, event budgets, callback lifetime and engine compiler consumption |
| ECS and SourceGen | 146 | Borrowed native memory, scheduling contracts, temporary identities and generated code |

Runtime targets remain .NET 10 and .NET Standard 2.1, with `LangVersion=latest`. Unity consumes precompiled DLLs. The generator targets .NET Standard 2.0 for compatible analyzer hosts. No runtime NuGet dependency was added by this review.

## Declare behavior without coordinating indices

The flat FSM still needs only a shared definition and the caller's four-byte StateId. Its construction can now state intent directly:

```csharp
var chest = new StateMachineBuilder();
var closed = chest.State();
var open = chest.State();
var opening = chest.Signal();
chest.StartIn(closed);
chest.Transition(from: closed, on: opening, to: open);
var definition = chest.Build();

// Resolve during setup. A chest's update needs no builder, runner or timer.
var openSignal = chest.Resolve(opening);
StateId state = definition.InitialState;
state = definition.Step(state, openSignal).CurrentState;
```

StatechartBuilder declares containment through `Region(parentState)`, and parallel activity through multiple regions of the same owner. Each region selects its initial state with StartIn. Registration order supplies table coordinates; application code does not coordinate array indices or counts. Build returns a copied immutable snapshot and can be repeated after changing the builder. Foreign and default construction handles reject use before mutation.

Named tree actions also avoid preorder offsets:

```csharp
var reload = BehaviorNode.Action(ReloadAction);
var compilation = new BehaviorTreeCompilation(
    BehaviorNode.Sequence(reload, BehaviorNode.Action(AttackAction)));
var setup = new TreeBehaviorBuilder<AiContext>(compilation, actions);
setup.Bind(compilation.GetAction(reload), reloadMachine);
TreeBehavior<AiContext> combat = setup.Build();
```

ReloadAction and AttackAction are application action IDs; actions handles unbound actions and predicates. reloadMachine is an independently configured child runtime. The compilation rejects ambiguous repeated use of the same Action expression. Distinct expressions with the same application ID identify distinct occurrences. Actions copied from a native compiled Subtree have no expression identity in the parent resolver; compose a child runtime when it needs its own named bindings.

TreeBehaviorBuilder validates bindings before adopting children. A failed Build leaves ownership unchanged; a successful Build consumes the builder. Definitions and runtimes do not retain the construction map, expressions or builder. Low-level table constructors and numeric bindings remain available for loaders.

The [character example](../samples/Bezoro.Statecharts.ConsoleDemo/README.md) declares Grounded and InAir locomotion alongside an independent damage-reaction region. The [NPC example](../samples/Bezoro.Behaviors.ConsoleDemo/README.md) binds BT -> statechart -> BT -> flat FSM -> BT without calculating node offsets. Runtime signal routing, memory/reactive tree semantics, shared budgets and activation cancellation keep their existing contracts.

## Correctness and work avoided

- Core pooling no longer creates a ConcurrentStack node on each return or repeatedly signals a full semaphore without waiters. Renting is distinct from Rented, preventing Validate/OnRent callbacks from returning an item before acquisition finishes. Failed factories wake waiters; teardown attempts owned cleanup even after callback failures.
- Singleton factories and lifecycle operations are serialized. Construction permission belongs to the factory's thread. Distinct overrides have explicit LIFO ownership and dispose once.
- Color parsing rejects malformed input; CSS and hex formatting write directly to spans. Comparisons, conversions, maps, capacity checks and CRC operations retain their contracts with checked arithmetic. ProcessAsync propagates cancellation and performs no mandatory per-cell yield.
- Chess load, refresh and manual moves use local rules. Analysis clients and classification run on explicit requests. Idle engines receive no stop/ready work during ordinary gameplay. Promotion keeps its UCI suffix and piece color. Captured PVs, analysis queues and caches have limits; async disposal joins workers, including transports with blocked stdin.
- Events expose dequeue budgets directly on IEventBus. Queued events no longer allocate a closure and delegate. Phase-bus disposal attempts every distinct owned bus after failures. Queues remain explicitly unbounded; consumption budgets do not bound retained work.
- Gameplay validates requests before mutation, releases completed activation callbacks, preserves accepted timer transitions and only writes streaming state on actual transitions. Input has an explicit ingestion budget and control cleanup. Timers reserve a world-owned pending queue on demand.
- Logging skips formatting and metadata when no sink observes the message. Async scopes restore the calling execution context and reject invalid nesting. LogPayload has a validated C# 9-callable constructor. Typing state has one coherent accepted-character position and named result factories.
- ECS cursor copies share a generation-checked lease. Active callbacks pin storage, preventing a copy from releasing borrowed native memory. Global structural/scheduler mutations reject concurrent batches before effects. Temporary handles are local to a recording and their global identity allocator never recycles tokens.
- Scheduler parallelism requires explicit access declarations or NoWorldAccess. Inferred types complement those declarations; inference alone cannot prove the effects of arbitrary C# helpers. Global structural operations require exclusive execution. Generated jobs, queries and split fields cover generic/nested types, keyword identifiers, effective accessibility and name collisions.
- Scheduler scratch is reused by its world and sized by active systems. Inactive execution does not reserve a command stream or command buffer. A context creates its stream on first access; recording the first command rents its buffer. Scheduler contexts expire when Update returns, and captured streams are never reactivated for another execution. Interval accumulation uses double internally to keep finite large inputs from creating infinite catch-up.
- Init-modifier type forwarding keeps .NET Standard consumer calls compatible when a host selects the .NET 10 libraries. A mixed-target execution regression covers this separately from compilation against one target.

## Migration

| Earlier API or behavior | Current contract |
| --- | --- |
| Coordinated state/region arrays and preorder binding offsets | Optional StateMachineBuilder, StatechartBuilder, BehaviorTreeCompilation and TreeBehaviorBuilder |
| ArrayExtensions.Process for asynchronous 2D work | ProcessAsync, with cancellation propagated |
| PoolOptions.ShrinkThreshold and AsyncWaitTimeout | Removed unused settings; configure actual capacity and per-call timed rental |
| IBoundedEventBus | IEventBus.FlushQueued(maxEvents) |
| Public mutable activation state/callbacks | Queue operations and read-only entry state |
| Float input time and squared streaming distances | Double values |
| LogPayload object initializer for required members | Validated four-argument constructor, with optional metadata in init properties |
| LogLevel numeric values | Success=2, Warning=3, Error=4, Exception=5; None=0, Info=1 and Divider=6 remain |
| IWordProvider and contradictory typing state/result constructors | IWordSource, TypingState(position, mistakeCount), named TypingResult factories and int positions |
| WorldOptions and CommandBuffer | WorldConfig and CommandStream |
| Mutating Get/TryGet aliases | Explicit Read/TryRead and Write APIs; cursor/accessor Get becomes Write |
| CommandStream.SetManaged | Set accepts any struct |
| Unconsumed observer stubs and the empty component catalog generator | Removed |
| Playable session requiring three clients | Playing client plus analysis client; default local construction starts neither |
| Reusing disposed match/analysis owners | Disposal is terminal; await DisposeAsync to join workers |
| Arbitrary Unity staging destinations | Dedicated directories under artifacts, without symbolic-link/junction ancestors |

Persisted application schemas must account for changed enums and field types. The framework does not silently migrate application-owned saves. Package versions continue to come from SemVer tags through the canonical resolver, and breaking commits carry Conventional Commit markers.

## Validation and measurement

The five behavior/condition module suites pass 228 tests in both normal and checked Release configurations. Independent probes compare 1,000 trees under small and large budgets, hierarchical chart dispatches, mixed nesting and lifecycle behavior. Collection tests show that live runtimes do not retain construction objects.

On the measured .NET 10 host, behavior runtime constructor allocations match the baseline: a tree runner uses 160 bytes with one or 10,000 alternatives; a statechart runner uses 392 bytes with two or 100,000 declared states; an unbound TreeBehavior uses 312 bytes. These constructor measurements include runtime objects and their storage, excluding definitions and construction graphs. They are workload-specific managed allocation figures, not universal actor sizes on Mono or IL2CPP.

The [Core diagnostic](../benchmarks/Bezoro.Core.Benchmarks/README.md) records warmed Rent/Return allocation changing from 32 to 0 bytes per operation. The [event probe](../benchmarks/Bezoro.Events.Benchmarks/README.md) records an 88-byte reduction per enqueue over 100,000 events, including queue growth. The [ECS workloads](../benchmarks/Bezoro.ECS.Benchmarks/README.md) distinguish indexed component windows from materializing every matching entity. Shared-host timing intervals are not frame-time guarantees.

The scheduler allocation probe measured 504 bytes per tick for one empty system and 1,720 for eight sequential empty systems before this review; both measured zero after warmup with the new executor. Creating a default-capacity stream without recording commands changed from 2,622,248 bytes in a cold process to 144 bytes. Requesting a stream still creates one object for that execution, and parallel execution has coordination costs. These are specific managed workloads on the .NET 10 host.

All four [C# 9 consumers](../samples/Bezoro.UnityApiSmoke/README.md) compile against the .NET Standard dependency closure with real Roslyn 3.8 and execute on the managed host with results 4, 4, 2 and 97. The helper matches SDK defaults for reference-assembly version unification warnings; other warnings remain errors.

The complete integrated Linux Release build passed with zero warnings/errors. All 3,351 tests passed across 19 suites, with no failures or skips, including Stockfish 19. The Core/foundations suites passed 1,473 cases per mode, behavior modules 228 and ECS 347 in both normal and checked builds. Logging also passed its 44 tests in Release and Debug. Runtime projects built for both targets; SourceGen built for .NET Standard 2.0.

Staging validates the canonical SemVer version, runtime dependency closure, 25 DLLs and 47 unique Unity metadata GUIDs. Compiler checks use the actual staged .NET Standard assemblies. Publication occurs only after main integration passes the complete suite on Linux and Windows; working-branch pushes do not run CI.

## Ownership and limits

Immutable definitions can be shared. Mutable owners need one execution thread or explicit synchronization. Independent actors/worlds can run on workers when their contexts and handlers support it; engine scene calls remain on the scene thread. Visit/event budgets bound library work units, not callback duration.

ECS relation targets consume component-catalog IDs; deleting targets or Reset does not recycle them. Capacity exhaustion rejects the new relation before changing its edge. Recreate the world to reset that catalog. Change tracking is opt-in and can synchronize bookkeeping between workers. Activation history and unbounded application-request queues need caller-chosen cleanup and backpressure.

Managed builds, reference models and compiler checks do not establish Unity Editor, IL2CPP, Godot export behavior, every possible thread interleaving, filesystem power-loss durability or proprietary Chess.com scoring equivalence. See each module's ownership and platform notes before choosing production limits.

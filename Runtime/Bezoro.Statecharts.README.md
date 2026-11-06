# Bezoro.Statecharts

Hierarchical state machines with orthogonal regions, explicit signals, typed guards, and synchronous lifecycle effects. The module targets .NET 10 and .NET Standard 2.1 and references StateMachines and Conditions. It has no engine, timer, task, or scene dependency.

The existing `Bezoro.StateMachines.StateMachineDefinition` remains the smaller choice for a chest with Open/Closed states. A caller can keep only its four-byte StateId and evaluate a signal against a shared definition. Select Statecharts when hierarchy, simultaneous regions, or lifecycle coordination are needed.

## Definitions and execution

Declare each state through its containing region. Each region requires an initial state and either a parent state or no parent. All root regions are active concurrently; entering a state enables all its child regions. Each enabled region contains one active state.

```csharp
using Bezoro.StateMachines;
using Bezoro.Statecharts;

var chart = new StatechartBuilder();
var motion = chart.Region();
var grounded = motion.State();
var inAir = motion.State();
motion.StartIn(grounded);

var groundMovement = chart.Region(grounded);
var idle = groundMovement.State();
var walking = groundMovement.State();
groundMovement.StartIn(idle);

// Reactions remain enabled while motion changes between Grounded and InAir.
var reaction = chart.Region();
var normal = reaction.State();
var takingDamage = reaction.State();
reaction.StartIn(normal);

var jump = chart.Signal();
var land = chart.Signal();
var walk = chart.Signal();
var damage = chart.Signal();
var recover = chart.Signal();
chart.Transition(from: grounded, on: jump, to: inAir);
chart.Transition(from: inAir, on: land, to: grounded);
chart.Transition(from: idle, on: walk, to: walking);
chart.Transition(from: normal, on: damage, to: takingDamage);
chart.Transition(from: takingDamage, on: recover, to: normal);
var definition = chart.Build();
SignalId damageSignal = chart.Resolve(damage);
StateId groundedId = chart.Resolve(grounded);

int context = 0;
var runner = new StatechartRunner<int>(definition);
runner.Start(ref context);
runner.Dispatch(damageSignal, ref context);
// Grounded, Idle, and TakingDamage are now active.
```

`StatechartBuilder` assigns the coordinated numeric state/region tables during construction. Its `StateHandle` and `SignalHandle` values belong to that chart, and a region accepts only its own states in `StartIn`. Default or foreign handles throw before inserting declarations. Each `Build` returns an independent immutable snapshot; later declarations or initial-state changes cannot alter it. Resolve runtime IDs during setup. The runtime retains neither the builder nor construction handles. The span-based `StatechartDefinition` constructor remains available for loaders with explicit tables.

Guards can inspect a `StatechartView` through `IStatechartHandler<TContext>.EvaluateGuard`. For Damage, a predicate can test `configuration.IsActive(groundedId)` to select an additive ground reaction, while another selects airborne knockback. All guards participating in one dispatch see the configuration before any selected exit.

Use `ConditionExpression` and `ConditionProgram` for short-circuit Boolean composition. Predicate identifiers are application-defined; the handler receives the typed context and can perform ordinary C# comparisons. Guards must be pure. The runtime may omit structurally preempted guards, and the condition program omits unreachable Boolean branches. There is no implicit guard-result cache between signals.

`StatechartHandler<TContext>` supplies optional lifecycle methods with empty implementations. Pass no handler when the chart needs no effects or predicates. A constant or short-circuited condition may execute without a handler; reaching an application predicate without a handler throws before configuration changes.

## Transition semantics

| Kind | Meaning |
| --- | --- |
| External | Replaces the common region. Targeting the source itself or one of its descendants exits and reenters the source and all its regions. Targeting an ancestor reenters that ancestor and its initial descendants. |
| Local | Requires a strict descendant target. It replaces only the first child region along the source-to-target path, preserving the source and the source's other child regions. |
| Internal | Requires source equal to target. It invokes only the transition effect, preserving all activations and descendants. |

Set `kind:` on `chart.Transition` or a `ChartTransition` constructor. External is the default. Direct jumps between orthogonal regions are rejected during definition construction. Coordinate independent regions by broadcasting a signal and guards rather than forcing one region's transition to enter another region.

Selection uses descending priority, then descending source depth, then declaration order. Each active source chooses its first passing guard in that order. Transitions conflict when their sources are ancestors/descendants, their exit domains overlap, or one exits the other's source. Disjoint regions may select transitions together. An enabled but conflicting transition consumes its source's choice; lower alternatives from that source do not become fallbacks after conflict resolution.

Only active sources with a matching signal enter the candidate heap. Once a selected transition structurally prevents another source from participating, that source's guards are skipped. This preserves the same pure-guard selection while avoiding work on known losing branches.

All selected exits run before transition effects and entries. Exits follow reverse compiled preorder, so descendants precede ancestors and later regions precede earlier ones. Transition effects follow selection order. Entries follow compiled preorder, so parents precede descendants and regions follow declaration order. A target's ancestry is entered explicitly, while its other child regions use their initial states.

## Lifecycle, ownership, and late results

`Start` enters the initial configuration; starting an active runner throws. `Stop` is idempotent, invalidates every activation before its first exit callback, and attempts every pending exit. StateId membership and ActiveStates may still describe other states awaiting exit during cleanup, but their asynchronous activation tokens are already invalid. Lifecycle failure uses the same global token invalidation. `Reset` stops and clears a fault after cleanup succeeds. Completed effects cannot be rolled back by the framework.

A guard exception preserves the prior active configuration and running status. An exception after lifecycle changes begin faults and stops the runner. Cleanup attempts exits for all states still active, including an entry whose OnEnter callback threw. The state and token are removed before OnExit, so that exit is never attempted twice. Cleanup errors are preserved with the original failure in an AggregateException. A faulted runner requires Reset before Start.

Operations reject reentrancy. Callbacks can query the configuration but cannot Start, Stop, Reset, or Dispatch recursively. State and reaction effects should produce application intents or commands; an application controller resolves movement, external impulses, restrictions, and animation layers explicitly.

A StateActivation identifies one entry in one runner. Dispatch revokes every token scheduled for exit before invoking its first exit callback. Tokens for unaffected states remain live, and tokens issued to new entries are live during their OnEnter callbacks. The exit plan uses existing bounded workspace; it creates no extra array. A token is invalid after exit, reentry, Stop, or use with another runner:

```csharp
var activation = runner.GetActivation(stateId);
// Schedule application work and remember this activation.
// When its result is delivered back to the owning thread:
if (runner.IsActive(activation))
    ApplyResult();
```

Tokens are process-local and do not belong in saved games. The serial is never reset; exhaustion rejects new entries before changing a configuration. `ActiveStates` and StatechartView are borrowed views and must not survive subsequent mutations. Definitions may be shared between threads. A runner, its context, and callback effects require one owner or external serialization.

## Polling and scheduled checks

The runner performs no work between explicit calls. The host may dispatch CheckMovement once per frame or schedule CheckVision at an interval with the existing Simulation TickScheduler. Those are ordinary signals with the same guard semantics. For repeated checks, include an active-state or changed-input predicate when reentry is not intended: an external transition to an already active state still explicitly requests exit and reentry. Cancel scheduled work on exit and validate its saved StateActivation before dispatching a delayed result. No polling state, clock, or timer array is added to runners that do not use scheduling.

## Memory and work

Immutable definition indices are shared by all runners. Let A be MaximumActiveStates, C be min(A, TransitionSourceCount), D be MaximumDepth, and S be GuardStackSize. The active configuration uses approximately 12A bytes plus two array headers. Signal candidates add 8C bytes. Charts with configuration-changing transitions add 12A + 4D bytes for reusable exit, entry, region, and path workspace. Guards add S Boolean elements only when needed. These figures exclude the runner and array headers, runtime alignment, and application handler/context memory.

No status or generation array is allocated for every declared state. Internal-only charts allocate no lifecycle plan/path arrays; charts without transitions allocate no candidate arrays. A definition with 100,000 alternative states and one active state has the same runner storage as a two-state definition with the same activity and transition needs. This is regression-tested using actual constructor allocations.

Active membership uses O(log A) search. A signal looks up matching transitions for active sources, orders relevant candidates with a bounded heap, and evaluates only guards that can still affect selection. Conflict resolution considers selected candidates. Configuration mutation shifts a small bounded active array; it does not scan inactive declarations. Construction, definition validation, and caller code have their own costs. Warm successful dispatch, entry, exit, and guard paths allocate no managed storage.

## Validation

The [tests](https://github.com/bezoro/csharp-framework/blob/7937b570b52cc850e10572251fb982c3d8d940f0/tests/Bezoro.Statecharts.Tests/README.md) cover locomotion/reaction parallelism, guard snapshots, local/external/internal semantics, lifecycle failure cleanup, identity, memory bounds, zero allocation, and independent concurrent owners. The [benchmarks](https://github.com/bezoro/csharp-framework/blob/7937b570b52cc850e10572251fb982c3d8d940f0/benchmarks/Bezoro.Statecharts.Benchmarks/README.md) define reusable diagnostic workloads. Engine/player validation is separate from managed compilation and tests.


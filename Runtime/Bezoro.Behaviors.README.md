# Bezoro.Behaviors

Optional composition of behavior trees, hierarchical statecharts, and the lightweight flat state-machine definition. The module references those runtimes and contains no Unity or Godot types. Its runtime targets are .NET 10 and .NET Standard 2.1.

Use the standalone flat machine for an open/closed chest. Use this module when an active state needs an owned behavior, or when a tree action needs to run a complete machine or another tree.

## Compose by occurrence

```csharp
using Bezoro.Behaviors;
using Bezoro.BehaviorTrees;
using Bezoro.StateMachines;

var reloadStates = new StateMachineBuilder();
var waiting = reloadStates.State();
var ready = reloadStates.State();
var reloadFinished = reloadStates.Signal();
reloadStates.StartIn(waiting);
reloadStates.Transition(from: waiting, on: reloadFinished, to: ready);
var reloadDefinition = reloadStates.Build();

// actions and context are supplied by the application.
// actions implements IBehaviorTreeHandler<AiContext> and keeps progress in the context.
var reloadAction = new TreeBehavior<AiContext>(
    new BehaviorTreeDefinition(BehaviorNode.Action(10)), actions);
var reloadMachine = new StateMachineBehavior<AiContext>(reloadDefinition,
    bindings: new[] { new StateBehaviorBinding<AiContext>(
        reloadStates.Resolve(waiting), reloadAction, reloadStates.Resolve(reloadFinished)) },
    successStates: new[] { reloadStates.Resolve(ready) });

var reload = BehaviorNode.Action(100);
var combatTree = new BehaviorTreeCompilation(BehaviorNode.Sequence(
    reload, BehaviorNode.Action(200)));
var combatBuilder = new TreeBehaviorBuilder<AiContext>(combatTree, actions);
combatBuilder.Bind(combatTree.GetAction(reload), reloadMachine);
var combat = combatBuilder.Build();

combat.Start(ref context);
var budget = new BehaviorBudget(64);
BehaviorStatus result = combat.Tick(ref context, ref budget);
// Success/Failure closes owned activity immediately and retains the result.
// Stop interrupts Running activity, or resets a terminal result before reuse.
combat.Stop(ref context);
```

`TreeBehaviorBuilder` resolves named actions through their originating compilation and converts them into compact runtime bindings. Default or foreign handles, repeated bindings, and duplicate children throw before changing the builder. A successful `Build` consumes the builder because the new runtime exclusively owns its children. A failed `Build` leaves the builder available and adopts no children. The resulting runtime retains neither the builder nor the compilation and its expression map. Release construction objects after setup.

The lower-level `TreeBehaviorBinding` constructor accepts an explicit compiled node ID for loaders with an established mapping. Its layout remains a child reference and an integer, 16 bytes on the tested x64 host. No handle or source-definition reference is added to each runtime binding. Both APIs identify a compiled node occurrence. Reusing an immutable subtree creates distinct occurrences, and each bound mutable child needs its own runtime. Bound actions delegate their complete lifecycle to the child. Unbound actions and predicates use the host `IBehaviorTreeHandler`.

`StateBehaviorBinding` associates a child with a state ID. `StatechartBehavior` starts it when that state enters and stops it before the host's exit callback. The host's entry callback runs before the child starts. Hierarchical parent states and orthogonal regions can each own children. `StateMachineBehavior` provides the same binding policy for a flat `StateMachineDefinition`; its external self transition stops and restarts the child.

Definitions can be shared between agents. A mutable child runtime belongs to one occurrence and is adopted permanently by its parent. Lifecycle operations on an owned child go through that parent. Constructors reject duplicate children, previously adopted children, invalid bindings, and exceeded limits before claiming any child. Custom leaf or composite implementations derive from `Behavior<TContext>` to use the same ownership and cleanup rules. `IBehavior<TContext>` is the root-facing execution contract.

## Completion and interruption

No final-state mapping means a machine continues Running. Configure success/failure state IDs when a machine is used as a finite task inside a tree. An active failure state takes precedence over an active success state in orthogonal configurations. The two mappings cannot contain the same state ID.

A child completing inside a state remains terminal until that state exits; it is not restarted or polled each update. Optional success/failure signal IDs request a one-shot transition. The statechart adapter records only bounded child completions, tags them with state activation tokens, and removes old entries after a transition or reentry. A delivered signal consumes one visit from the shared allowance and is also limited by `maximumCompletionsPerTick`. No timer or periodic polling is installed.

`Dispatch` delivers explicit host signals while the composition is idle. Serialize signals and ticks externally. A descendant signal reserves its owning ancestor chain until callbacks and cleanup finish. Attempts to mutate an ancestor or signal a sibling from those callbacks fail before changing lifecycle state or child tracking; internal child entry/exit remains available. This reservation uses existing parent links and one flag per component, with no allocation or scan of inactive siblings. Queue callback-generated signals for delivery after the current operation returns.

`StatechartBehavior.TryDispatch(token, signal, ref context)` rejects results from an exited, foreign, stopped, or terminal activation. The flat adapter exposes an instance-local `ActivationVersion`; retain that adapter reference as well as the version when tagging a delayed result.

Success and Failure automatically stop all started descendants before Tick returns, including siblings in other chart regions. The result remains latched; further ticks consume no visits. Statechart `ActiveStates` is empty after terminal cleanup. The flat adapter retains `CurrentState` for inspection. Stop after completion resets the result without repeating cleanup.

Stopping, preemption, and faults invalidate running action/state tokens. Token checks also validate each ancestor binding scope, so a nested action becomes logically inactive before its physical cancellation if an ancestor state is already scheduled to exit. Orthogonal regions outside that exit domain remain live. Structural state-membership views can still show a partially unwound configuration during callbacks; use activation tokens when accepting delayed work. Cleanup is attempted once per activation, before callbacks can be retried, and every started child is attempted even if another cleanup callback throws. Callback effects cannot be rolled back. A failing Tick or lifecycle signal faults its component; an explicit Stop resets it for reuse. Pure guard exceptions from an explicit chart Dispatch preserve its configuration. Reentrant mutation is rejected.

## Shared work allowance

All nested runtimes receive the same `ref BehaviorBudget`. An adapter never creates or refunds an allowance for a child. `IsBudgetSuspended` distinguishes unfinished evaluator work from a normal Running action that is waiting for game data.

If a tree action reaches a child after spending the last visit, its adapter retains a continuation with the parent's activation token. On the next quantum it resumes that child before charging the parent's next visit. This preserves progress at budget one through deep nesting. A normal Running yield returns control to the parent's next reactive evaluation; a genuinely suspended evaluation retains its exact continuation. Thus a small allowance introduces explicit priority latency rather than repeatedly recomputing a prefix.

Orthogonal state bindings use a persistent round-robin phase. A phase can span updates and ends after its active children each receive their quantum. State changes replace that phase, and queued old activations cannot resume afterward. Each child is updated at most once per adapter phase. Conditions and synchronous chart transitions are atomic operations with their own definition bounds; visits do not measure wall-clock time or arbitrary host callback cost.

## Memory and ownership costs

Composition is opt-in. The flat kernel still stores only the current state value chosen by its host. Adding an adapter adds one runtime object, lifecycle/ownership metadata, and its configured binding storage. Every configured child instance retains its preallocated workspace for later activation, including while inactive. This reserves predictable capacity and avoids allocations on state changes; it is not a claim that the complete graph's memory is only its current path.

Bindings and terminal maps are sparse. No adapter allocates a Cartesian state/signal table or one row for every declared state. The statechart adapter's active binding list is bounded by `min(bindingCount, MaximumActiveStates)`. Completion rows are bounded by that peak and the number of bindings with completion signals. `CompletionCapacity` is zero when no binding emits a signal. Cleanup follows the intrusive list of children actually started, rather than scanning configured inactive children.

Tree internals reserve their maximum active path and condition scratch as described by the tree module. Definitions remain shared and immutable. The composition's depth defaults to 32 and can be configured up to the supported ceiling of 64; this bounds managed adapter recursion. The default component limit is 4096 and can be configured explicitly. Limits count runtime adapters, not compiled tree nodes or possible state identifiers. The graph is immutable and singly owned, so cycles cannot be introduced through the supported constructors.

Successful Start, Tick, Dispatch, and Stop paths use their existing storage. Construction, validation failures, exceptions, host callbacks, and application serialization have their own allocation costs. Separate owned graphs may execute concurrently if their contexts and handlers also permit it; one graph requires one owner or external synchronization. Engine scene access belongs to the engine thread.

## Validation

The [test suite](https://github.com/bezoro/framework/blob/aa0b79b77bc4eb913707aac088c28f3a333d15a4/tests/Bezoro.Behaviors.Tests/README.md) covers all composition directions, budget-one continuations, parallel fairness, tokens and one-shot completions, fault cleanup, constructor ownership, reentrancy, and warmed allocation checks. The [benchmarks](https://github.com/bezoro/framework/blob/aa0b79b77bc4eb913707aac088c28f3a333d15a4/benchmarks/Bezoro.Behaviors.Benchmarks/README.md) use preallocated graphs and contexts. Managed tests do not establish Unity player, IL2CPP, or Godot export behavior.


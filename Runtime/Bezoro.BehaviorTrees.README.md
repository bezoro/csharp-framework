# Bezoro.BehaviorTrees

Memory-aware behavior trees with immutable definitions and one execution owner per agent. The module targets .NET 10 and .NET Standard 2.1 and depends only on `Bezoro.Conditions`. It has no statechart, Unity, Godot, thread-pool or coroutine dependency.

## Define once, execute per agent

```csharp
using Bezoro.BehaviorTrees;

var definition = new BehaviorTreeDefinition(
    BehaviorNode.Sequence(
        BehaviorNode.Condition(predicateId: 0),
        BehaviorNode.Action(actionId: 1)));
var runner = new BehaviorTreeRunner<AgentContext>(definition);
runner.Start();

// The host owns context and handler. Reuse the same logical handler during an activation.
var budget = new BehaviorBudget(maximumVisits: 32);
BehaviorTickResult result = runner.Tick(ref context, handler, ref budget);
```

The handler implements `IBehaviorTreeHandler<TContext>`: `Evaluate` receives a predicate ID and a readonly typed context; `Enter`, `Tick`, `Exit` and `Abort` receive the action ID, compiled node occurrence, activation token and mutable context. `Tick` returns `Running`, `Success` or `Failure`. Struct handlers can use the overload taking `ref handler` to avoid boxing at the call site and in null validation, including on runtimes that do not eliminate boxing through JIT specialization.

Each agent owns its current context and runner. The same immutable definition can serve multiple agents on separate workers. A handler may be shared only when its own state permits it. The host serializes access to one runner, including `IsActive` checks when accepting asynchronous results.

## Control-flow policies

| Node | Policy |
| --- | --- |
| `Sequence` | Resume the current child. Stop on failure; succeed after all children succeed. An empty sequence succeeds. |
| `Selector` | Resume the current child. Stop on success; fail after all children fail. An empty selector fails. |
| `ReactiveSequence` | Recheck earlier children before continuing the running branch. A failed prerequisite interrupts that branch. |
| `ReactiveSelector` | Recheck higher-priority children before continuing the running branch. A newly eligible action interrupts the old action before its own entry. |
| `Invert` | Swap success and failure; preserve running. |
| `Repeat` | Repeat a successful child a positive, finite number of times. A failed child ends the repeat immediately. |
| `Condition` | Evaluate one application predicate or one compiled `ConditionProgram`. |
| `Subtree` | Execute a separate occurrence of another compiled tree. |
| `Action`, `Succeed`, `Fail` | Execute an application leaf or return a fixed result. |

Memory sequences and selectors do not reevaluate completed conditions, actions or unrelated siblings while their active child runs. Reactive variants are explicit because priority checking has a cost. Pure prerequisite checks do not interrupt an existing action by themselves. Reaching a different action is a preemption attempt: the previous action is aborted before the candidate's `Enter`. If that candidate then fails, a fallback may start again; aborted branch progress is discarded.

Reactivity preserves memory within an unchanged running branch. A sequence nested beneath a reactive parent keeps its completed work as long as the same action remains active. The runtime maintains at most one action activation per tree. Orthogonal behaviors can be coordinated by the separate statechart/composition modules.

## Cooperative work allowance

A new control node, condition, constant or action update consumes one visit before evaluation. Completed control frames retain their cursors rather than spending the same visit again after a yield. A one-visit allowance therefore progresses through a deep path instead of repeatedly restarting at the root. `BudgetExhausted` distinguishes a cooperative yield from an action returning `Running`. Zero allowance changes no progress and calls no handler.

The allowance is passed by reference so an adapter can charge nested work to the same budget. A callback still owns its own computational cost; a visit is not a duration limit. One complete `ConditionProgram` is an atomic visit. Its explicit `InstructionCount` bounds that work, and its short-circuit jumps skip unselected predicates. A predicate ID alone uses no condition scratch storage.

A suspended evaluation resumes its exact point even if application data changes between updates. The runtime does not copy the context or recheck already evaluated prerequisites mid-cycle. This policy avoids starvation under small allowances. A reactive cycle begins again after reaching its running action. Urgent interruptions can use `Stop` from the owning execution loop. Do not call a runner recursively from its callbacks.

## Lifetimes and failures

`Start` activates an inactive runner; it does not run actions until their first budgeted visit. A running action receives one `Enter` followed by its updates, then one terminal `Exit` or interruption `Abort`. Its token is invalidated before exit/abort callbacks. Completion and interruption never share the same activation token with later work. Check `IsActive(token)` on the owning thread before accepting a delayed job result.

Terminal results are sticky. Further ticks consume no visits and execute no callbacks until `Stop`/`Start` or `Restart`. `Stop` is idempotent after cleanup. `Restart` interrupts any running action and discards its progress.

Handler exceptions and invalid action results fault the execution. The runtime attempts to abort a still-active action once; if that cleanup also fails, an `AggregateException` preserves both failures. An action whose terminal `Exit` already began is not aborted again. Host cleanup callbacks must release their own resources even when they throw. A faulted runner must be stopped before starting again. Reentrant operations throw.

## Memory and compilation

Compilation uses iterative traversal and validates explicit expanded-node/depth budgets. Construction expressions own copies of their child arrays. Reused expressions and `Subtree` references expand into distinct preorder occurrences, preserving action IDs but assigning unique node IDs. Internal subtree metadata is copied during compilation; agents then share the resulting compiled definition. The compiler does not retain the construction expression graph.

The definition stores approximately 16 bytes per expanded node plus condition-program references and array/object headers. A runner allocates 16-byte frames proportional to the definition's actual maximum depth, not its node count. It adds a path snapshot only as deep as action paths containing a reactive ancestor. Unrelated deep memory branches and purely conditional paths do not increase that snapshot. A bool scratch array is allocated only when compiled condition programs need it. A sequence with 10,000 inactive siblings uses the same runner frame capacity as a sequence with one sibling at the same depth.

Ticks and stops inspect the active path, not every node in the definition. No per-node progress/generation arrays, tasks, timers, closures or locks are retained. Creation, compilation, exception handling and application jobs are outside the allocation-free steady-state claim. Owner identities and 64-bit action serials are never reused; exhaustion throws rather than reviving an old token.

## Resolve named action occurrences

When another runtime needs to bind a particular action, retain its construction expression in a variable and compile through `BehaviorTreeCompilation`:

```csharp
var reload = BehaviorNode.Action(actionId: 10);
var compilation = new BehaviorTreeCompilation(BehaviorNode.Sequence(
    BehaviorNode.Condition(predicateId: 0), reload));
BehaviorTreeDefinition definition = compilation.Definition;
BehaviorActionHandle reloadAction = compilation.GetAction(reload);
int nodeId = compilation.Resolve(reloadAction);
```

The handle identifies that occurrence even when preceding branches change size. `GetAction` requires an Action expression that occurs exactly once in this construction graph. Declare separate Action expressions when two occurrences share an application action ID. Reusing the same expression twice is ambiguous and throws. An action copied from a compiled `Subtree` has no construction-expression identity in the outer compilation. Model a separately bound subtree as its own runtime through [Behaviors](https://github.com/bezoro/framework/blob/aa0b79b77bc4eb913707aac088c28f3a333d15a4/src/Bezoro.Behaviors/README.md), or use explicit compiled IDs when loading an established occurrence mapping.

`Resolve` rejects default handles and handles from another compilation. The resolver owns the action-expression map. The definition and runners retain no expression graph or map; release the compilation and handles after setup. A standalone `BehaviorTreeDefinition` does not create this map. `TreeBehaviorBuilder` in the optional composition module accepts these handles and stores only compact node IDs in runtime bindings.

See the [tests](https://github.com/bezoro/framework/blob/aa0b79b77bc4eb913707aac088c28f3a333d15a4/tests/Bezoro.BehaviorTrees.Tests/README.md) and [benchmarks](https://github.com/bezoro/framework/blob/aa0b79b77bc4eb913707aac088c28f3a333d15a4/benchmarks/Bezoro.BehaviorTrees.Benchmarks/README.md). Managed compilation and tests do not establish Unity player, IL2CPP or Godot export behavior.


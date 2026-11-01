# Composable NPC behaviors

```sh
dotnet run -c Release --project samples/Bezoro.Behaviors.ConsoleDemo
```

The example runs a tree that owns a statechart. Patrolling owns a memory tree that continues walking without reevaluating its successful prefix. At 500 ms, a scheduled perception signal changes the mode to Combat and aborts the walking activation once. Combat owns a tree containing a flat aiming state machine, which itself owns a tree action. Another compiled subtree performs the attack. A completion signal finishes the mode and propagates success through the graph.

The nesting is BT -> statechart -> BT -> flat FSM -> BT, plus a native BT subtree. One visit budget is forwarded by reference across the graph. The C# 9 consumer additionally checks progress with one visit per update. Runtime children are configured once and owned by one occurrence; immutable definitions can be shared by other actors with their own runtimes.

State and chart builders describe containment and transitions through construction handles. A named Action expression is resolved by BehaviorTreeCompilation and bound through TreeBehaviorBuilder, so adding an earlier node cannot silently redirect a child binding. These construction objects are not retained by the runtime. The example also executes the C# 9 consumer for the remaining framework modules.

Perception is scheduled separately from behavior execution. Only the signal invokes the vision predicate. Mutable runtime access is serialized by this host; engine physics, scene access and perception queries belong to application adapters. Setup allocations and application instrumentation are not included in the libraries' steady-state allocation claims.

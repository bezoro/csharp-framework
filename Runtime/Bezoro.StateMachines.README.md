# Bezoro.StateMachines

Immutable finite-state definitions with pure evaluation. The module stores state transitions as data. It has no callbacks, reflection, background loop, ECS dependency, or engine dependency.

## Define and advance an agent

```csharp
using Bezoro.StateMachines;

var machine = new StateMachineBuilder();
var idle = machine.State();
var chasing = machine.State();
var attacking = machine.State();
var spotted = machine.Signal();
var reached = machine.Signal();
var lost = machine.Signal();

machine.StartIn(idle);
machine.Transition(from: idle, on: spotted, to: chasing);
machine.Transition(from: chasing, on: reached, to: attacking);
machine.Transition(from: chasing, on: lost, to: idle);
machine.Transition(from: attacking, on: lost, to: idle);
var definition = machine.Build();
SignalId spottedSignal = machine.Resolve(spotted);

// One definition can serve many agents. Each agent owns its current state.
StateId current = definition.InitialState;
StateStep step = definition.Step(current, spottedSignal);
current = step.CurrentState;
if (step.HasStateChanged)
    ApplyStateChange(step.PreviousState, step.CurrentState);
```

The host supplies signals after evaluating game conditions and applies the returned state explicitly. `Step` neither changes the definition nor retains agent state. It does not automatically start at `InitialState`, queue signals, or execute entry/exit actions. Those choices stay in the host, where scene-object access and command ordering are known.

`HasTransition` distinguishes an explicit matching rule from a missing rule. A missing rule preserves the current state. A self transition has `HasTransition == true` and `HasStateChanged == false`. The host can deliberately respond to that self transition when a restart or repeated action is useful.

## IDs and validation

`StateMachineBuilder` assigns counts and identifiers as states and signals are declared. `StartIn` is required, even for a one-state machine. `StateHandle` and `SignalHandle` belong to that builder; default and foreign handles throw before changing declarations. `Resolve` converts a handle into its stable runtime ID. Resolve IDs used by the host once during setup. A builder can produce multiple immutable snapshots; later declarations and changes to `StartIn` do not change earlier definitions. New handles only belong to snapshots built after their declaration.

The definition does not retain the builder or its handles. The span-based `StateMachineDefinition` constructor remains useful for loading transition tables with an established numeric mapping. Both construction paths use the same sparse kernel and validation.

State and signal IDs contain a nonnegative `int` and occupy four bytes. Their default value is zero, which identifies the first state or signal. State IDs are valid within `[0, StateCount)`, and signal IDs within `[0, SignalCount)`. The definition requires at least one state and permits zero signals for a machine accepting no input. A zero-signal machine has no valid query signal.

IDs have no owner identity. An ID obtained from another definition is accepted when its integer is within this definition's range. Applications choose consistent semantic mappings when saving states or connecting systems. An integer from a previous version of an application's state graph may need an application-owned migration.

Construction rejects transitions outside the declared ranges and duplicate source/signal pairs, even when their targets agree. There is no priority ordering or implicit fallback rule. `TryGetTransition` returns false only for a valid pair with no rule; invalid query IDs throw `ArgumentOutOfRangeException`. Its default output on a miss is not evidence that state zero has a matching rule.

## Storage and concurrency

Construction copies and sorts the supplied transitions by source and signal. The caller may reuse or change the input array afterward. The read-only `Transitions` view exposes that stable order. A transition occupies 12 bytes; storage is approximately `12 × transitionCount` bytes plus array/object headers. There is no state-by-signal matrix or separate allocation per state. Declaring a large ID range does not allocate storage for every possible pair.

Construction takes O(n log n) time. `Step` and `TryGetTransition` use O(log n) binary search, allocate no managed storage, and have no mutable cache. An empty table is a constant-time miss after ID validation. Each `StateStep` is a 16-byte readonly value.

Definitions can be queried concurrently by independent agents. Store each agent's current `StateId` with its other simulation data and serialize mutations of that agent externally. A thread-safe definition does not make host actions or scene objects thread-safe. Unity and Godot calls still belong on their required engine threads.

Use a new definition when rules change; an existing definition remains an immutable snapshot. This module implements flat finite-state machines. Hierarchical states, timed transitions, behavior trees, and predicate callbacks are outside its contract.

## Validation

```sh
dotnet test tests/Bezoro.StateMachines.Tests/Bezoro.StateMachines.Tests.csproj -c Release
dotnet run -c Release --project benchmarks/Bezoro.StateMachines.Benchmarks -- --filter '*StateMachineBenchmarks*'
```

Tests compare 100,000 seeded steps with an independent dictionary model, check concurrent use by 32 agents, and measure managed allocations after warm-up. They also cover copied input, duplicate rules, self transitions, missing rules, boundary IDs, and extremely large declared ranges. Benchmark workloads are described in the [benchmark README](https://github.com/bezoro/framework/blob/a517911092a882b4d8b6efada0488da063272c56/benchmarks/Bezoro.StateMachines.Benchmarks/README.md). Managed tests do not establish engine-player or IL2CPP compatibility.

## Choosing a larger runtime

This flat kernel stays dependency-free and stores no agent runtime, timer, or callback. A host can keep only a four-byte StateId and call Step on explicit events, as in the [chest example](https://github.com/bezoro/framework/blob/a517911092a882b4d8b6efada0488da063272c56/samples/Bezoro.Statecharts.ConsoleDemo/README.md).

For hierarchical states and orthogonal regions, choose the optional [Statecharts module](https://github.com/bezoro/framework/blob/a517911092a882b4d8b6efada0488da063272c56/src/Bezoro.Statecharts/README.md). For AI composition, [Behaviors](https://github.com/bezoro/framework/blob/a517911092a882b4d8b6efada0488da063272c56/src/Bezoro.Behaviors/README.md) can adapt this same flat definition to an owned behavior node. These choices add the workspace and lifecycle features described by those modules; using the flat kernel does not construct them.


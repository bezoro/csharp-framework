# Composition benchmarks

The benchmarks build graphs and contexts once. `NestedBudgetOne` advances exactly one shared allowance through depths 1, 8 and 32; a leaf is not expected to update on every invocation at that allowance. `NestedFullPath` gives the same graph 128 visits. The separate `StateMachineCompositionBenchmarks.TransitionAndTick` case has no depth parameter and measures explicit state changes between two owned tree runtimes and one update of the new child.

```sh
dotnet run -c Release --project benchmarks/Bezoro.Behaviors.Benchmarks -- --filter '*CompositionBenchmarks*'
```

Setup, definition compilation, graph validation, context construction, and user job scheduling are outside measured operations. A warmed zero-allocation test is included in the test suite. Defining these workloads does not imply a latency result; record hardware, runtime, source revision and error intervals before using measurements for a game budget.

Setup uses construction builders and named action handles. The measured runtime retains compact bindings and the same immutable definitions; builders, handles, and occurrence maps take no part in measured calls.

## Recorded diagnostic

[Raw measurements and context](../results/2026-10-30-composable-behaviors/README.md) record these workloads with source revision, parameters, host/runtime and error intervals. The measurements cover steady-state data execution after setup.

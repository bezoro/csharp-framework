# Statechart benchmarks

The sparse benchmark compares two declared states with 100,000 alternatives, while exactly one state is active. Matching signals perform one external transition; unmatched signals find no matching outgoing edge. Definition/runner construction and application callbacks are outside measurements.

```sh
dotnet run -c Release --project benchmarks/Bezoro.Statecharts.Benchmarks -- --filter '*StatechartBenchmarks*' '*OrthogonalDispatchBenchmarks*'
```

The workload makes inactive declaration count visible rather than assuming every state needs per-frame work. Runtime metadata and hardware still affect cache behavior. Record source revision, runtime, parameters, and error intervals before using measurements for frame budgets; .NET 10 measurements do not establish Unity/IL2CPP or Godot timing.

## Recorded diagnostic

[Raw measurements and context](../results/2026-10-30-composable-behaviors/README.md) record the sparse workloads with source revision, parameters, host/runtime and error intervals. The measurements cover steady-state data execution after setup.

OrthogonalDispatchBenchmarks varies active width from 1 to 512 and measures internal broadcast and external reentry. StatechartBenchmarks varies inactive declaration count at fixed active width.

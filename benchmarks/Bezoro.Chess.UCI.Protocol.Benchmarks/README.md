# Chess protocol benchmarks

These benchmarks separate local gameplay and rules from engine startup and search. `LocalGameplayBenchmarks` measures move application, opening legal moves, and eight local plies. `LocalRulesBenchmarks` measures generation and tactical classification with fresh or retained legal-move lists. `UciInfoParsingBenchmarks` checks both parser entry points against the same three-move variation before measuring them.

Run an optimized measurement with allocation diagnostics:

```sh
dotnet run --project benchmarks/Bezoro.Chess.UCI.Protocol.Benchmarks -c Release -- --filter '*LocalGameplayBenchmarks*' --job short
```

For isolated review output, pass `--artifacts-path` to `dotnet run` and `--artifacts` to BenchmarkDotNet. `--job Dry --inProcess` is a smoke check. Its single cold iteration includes startup and JIT effects, so it cannot establish throughput, allocation guarantees, or a performance comparison.

The API review smoke check ran all three local-gameplay workloads on .NET 10.0.7, SDK 10.0.203, Debian 13, and an Intel Xeon Platinum 8573C with five visible logical CPUs. The environment denied elevated process priority. No before/after performance claim comes from this run. Engine review benchmarks require separate finite Stockfish search, CPU, and hash budgets because engine cost does not describe local move latency.

# Logging benchmarks

`LoggerAllocationBenchmarks` compares plain logging with caller-created nested async contexts. `HasSubscriber` includes the unobserved path, which skips formatting and metadata. Nested context scopes still allocate because the benchmark explicitly creates them even without a sink.

```bash
dotnet run --project benchmarks/Bezoro.Logging.Benchmarks -c Release -- --filter '*LoggerAllocationBenchmarks*' --job short
```

The benchmark project defines `DEBUG` so conditional log calls remain in Release measurements. The referenced logging assembly uses its normal Release compilation. The measurements do not describe engine sinks, dispatcher work, file I/O, or application argument builders.

# Events benchmarks

`EnqueueAndFlush` measures explicit batches of struct events with no subscribers. It includes typed dispatch-object allocation and the returned publication contexts. The bus reuses its queue capacity after warmup. Batch size changes through BenchmarkDotNet parameters; memory figures are per benchmark invocation.

```bash
dotnet run --project benchmarks/Bezoro.Events.Benchmarks -c Release -- --filter '*EnqueueAndFlush*' --job short
```

A separate finite allocation probe compared commit `23d613a9` with this implementation on .NET 10.0.7, Linux x64, Intel Xeon Platinum 8573C. After warming 10,000 enqueues and flushes, each of five samples created a new bus and measured `GC.GetAllocatedBytesForCurrentThread()` around 100,000 enqueues of a four-byte event. It measured 14,098,944 bytes before and 5,298,944 bytes after, including queue growth. The reduction was 88 bytes per event. A typed queued object replaces the closure, delegate, and wrapper previously created for each enqueue.

The finite probe retains every event until after measurement; the BenchmarkDotNet workload above drains each batch. These measurements describe managed allocation for those .NET workloads. They do not establish latency, concurrent throughput, Unity Mono, IL2CPP, Godot, or frame-time results.

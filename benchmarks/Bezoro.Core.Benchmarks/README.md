# Core benchmarks

BenchmarkDotNet workloads cover object pools, swap-back storage, deterministic random draws, weighted selection, shuffling, generational slot maps, and ring buffers.

```sh
dotnet run -c Release --project benchmarks/Bezoro.Core.Benchmarks -- --filter '*DeterministicRandomBenchmarks*' '*GameCollectionsBenchmarks*'
```

## Recorded foundation diagnostic

The [random CSV](results/2026-10-29-random-short-run.csv) and [collection CSV](results/2026-10-29-collections-short-run.csv) were collected on Debian 13, Intel Xeon Platinum 8573C, SDK 10.0.203 and .NET 10.0.7. Three warmups and three 100 ms measured iterations were used. Implementations correspond to `045cdad5` and `f169f62d`.

All ten workloads reported zero managed allocation per operation after setup. Weighted selection from 256 entries measured about 53.5 ns; shuffling 32 entries measured about 111.5 ns. Smaller operations approach the measurement floor and have wide relative error intervals. Inspect the raw data and method batching before comparing them. These short shared-host runs do not establish Unity/Godot timing guarantees.

## Recorded toolkit diagnostic

[Selected measurements and raw reports](../results/2026-10-30-toolkit/README.md) record the source revision, runtime, workload parameters and error intervals. Only the listed workloads were measured; the project also defines other profiling cases.

## Pool reuse allocation diagnostic

The [2026-10-31 samples](results/2026-10-31-pool-reuse-diagnostic.csv) compare the available-reference storage before and after replacing `ConcurrentStack<T>` nodes with reusable stack storage protected by the existing lifecycle gate. A Release .NET 10.0.7 console loop on the Intel Xeon Platinum 8573C shared host used one prewarmed `object`, maximum capacity one, statistics disabled, and async waiting disabled. .NET reported four usable processors. Each phase warmed one million rent/return pairs, then measured five batches of two million pairs with `Stopwatch.GetTimestamp` and current-thread allocated bytes. One operation is a complete pair.

Allocation fell from 32 bytes per pair to zero. Recorded times ranged from 524 to 1053 ns before and 167 to 185 ns after. The phases ran sequentially while other review builds and tests shared the host; these samples support the allocation finding and do not establish a speedup or contention behavior. The xUnit allocation regressions also cover reuse with async waiting enabled and immediately successful timed rents. Actual waits, new owned objects, storage growth, and handle creation have separate allocation costs.

Use `--filter '*ObjectPoolBenchmarks*'` for a BenchmarkDotNet run on the target runtime. Include the application's concurrency pattern when assessing the storage lock.

## Concurrent ring boundary diagnostic

The [2026-11-11 report](results/2026-11-11-concurrent-ring-short-run.csv) measures one enqueue/dequeue pair after synchronizing ring operations. BenchmarkDotNet 0.14.0 ran its ShortRun job (one launch, three warmups and three measured iterations) on Debian 13, Xeon Platinum 8573C, SDK 10.0.203 and .NET 10.0.7; the host reported five usable processors. The measured Core implementation is `2a7516d0`.

The pair measured 55.41 ns mean, 2.549 ns standard deviation and zero managed allocation. The 99.9% error half-width was 46.51 ns, so this short shared-host diagnostic is not a precise throughput guarantee or a contention benchmark. The former owner-only ring has a different safety contract; its earlier measurements are not evidence of a concurrency speedup. Existing allocation tests cover the synchronized queue and enumerator after warmup.

```sh
dotnet run -c Release --project benchmarks/Bezoro.Core.Benchmarks -- --filter '*GameCollectionsBenchmarks.RingBufferEnqueueDequeue*' --job short
```

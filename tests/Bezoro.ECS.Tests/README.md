# Bezoro.ECS.Tests

Runtime, source-generator, and API-contract tests for ECS ownership, storage, queries, commands, resources, and scheduling.

## Run

```bash
dotnet test tests/Bezoro.ECS.Tests/Bezoro.ECS.Tests.csproj -c Release --artifacts-path /workspace/scratch/api-review/ecs/artifacts
dotnet test tests/Bezoro.ECS.Tests/Bezoro.ECS.Tests.csproj -c Release --artifacts-path /workspace/scratch/api-review/ecs/checked-artifacts -p:CheckForOverflowUnderflow=true
```

Build `src/Bezoro.ECS/Bezoro.ECS.csproj` separately to cover both .NET 10 and .NET Standard 2.1. The analyzer builds as .NET Standard 2.0. Keep artifact directories separate when validating concurrently.

## Contracts covered

| Area | Evidence |
| --- | --- |
| Native/managed storage | Bounds before unsafe access, disposed columns, unsupported Marshal layouts, generic value types, archetype compaction and transitions. |
| Cursor lifetime | Copies share disposal, old copies cannot release a reused lease, callback/job pinning, default-handle errors, indexed identity access across chunks/archetypes. |
| Structural exclusion | Direct delegates, sequential/parallel jobs, active cursors, and concurrent attribute-only system batches reject invalidation before storage changes. |
| Commands | Stream/world ownership, per-stream/per-recording temporary identity, sentinels, lazy identity epochs, exhaustion before recording/type registration, payload overflow diagnostics, failure cleanup, lazy command-buffer rental, and scheduler context epochs. |
| Playback oracle | 7,680 seeded mixed managed/unmanaged set/remove commands match a separate sequential reference model across small chunks and repeated transitions. |
| Resources | Idempotent disposal, all-resource cleanup after exceptions, replacement identity, caller ownership after rejection, factory/disposal reentrancy, stable restored boxes under concurrent first access. |
| Change tracking | Mutable access, read-only traversal, incremental query windows, disjoint parallel writers, unique versions beyond the 32-bit boundary, zero-allocation steady-state paths. |
| Scheduling | Phase/stage validation, declared-order precedence with resource conflicts, unknown-access exclusivity, explicit `NoWorldAccess` parallelism, nested scheduling, interrupted joins and worker failures. |
| Relations | Incoming-edge cleanup and exhaustion of retained per-target marker IDs, including after Reset. |
| Snapshots | Deny-by-default types, explicit allow-lists, malformed payload rejection before mutation, reader/writer contracts, active-borrow exclusion. |

`SourceGeneratorCompilationTests` runs all four generators through Roslyn and compiles their output. Cases include generic and nested generic jobs, constraints, record queries, escaped identifiers, hint/helper collisions, effective accessibility, unsupported-declaration diagnostics, cursor/accessor access inference, effective overrides, inherited generic and explicit interface updates, and a base compiled in a separate assembly. Metadata tests emit assemblies and inspect the generated contracts. Inferred-only systems remain exclusive regardless of helper/constructor/initializer shape, while explicit declarations retain the parallel path; direct structural calls remain exclusive despite component annotations.

The temporary-identity boundary collection disables test parallelism while injecting/restoring the global epoch boundary. Other concurrency tests join their workers before returning.

Allocation tests warm measured paths before checking steady-state allocations. Their zero-byte contracts do not establish throughput. Scheduler probes use synchronization to detect overlap; benchmark timing belongs in `benchmarks/Bezoro.ECS.Benchmarks`.

Compatibility-only tests were removed with the obsolete APIs. Behavioral coverage now exercises `WorldConfig`, `CommandStream`, `Read`/`Write`, and `QueryView`; no test requires a removed forwarder or observer stub.

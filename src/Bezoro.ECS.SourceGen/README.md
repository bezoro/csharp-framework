# Bezoro.ECS.SourceGen

Four Roslyn incremental generators support ECS queries, systems, jobs, and split-field components. The analyzer targets .NET Standard 2.0. Consumer runtime assemblies target .NET 10 and .NET Standard 2.1.

## Declare a query

```csharp
[Query, With(typeof(Position)), Without(typeof(Frozen))]
internal readonly partial struct MovingEntities;
```

`QuerySourceGenerator` implements `ICompiledQuerySpec.Build` with deterministic filters. Supported attributes are `With`/`All`, `Without`/`None`, `AnyOf`/`Any`, `Optional`, `Changed`, and `Added`. Query declarations must be top-level partial structs or record structs. Generic declarations and escaped identifiers are supported. Ref structs, file-local declarations, and nested queries require a manual `ICompiledQuerySpec` instead.

Component filter types must be closed non-nullable value types that the world can store. The generator reports unsupported declarations at the declaration itself:

| Diagnostic | Meaning |
| --- | --- |
| `BECSG001` | Unsupported ECS attribute on a generated query. The attribute is ignored. |
| `BECSG002` | Query declaration cannot be completed by the generator. |
| `BECSG003` | Query filter component cannot be stored in a world. |
| `BECSG004` | Split component or grouped field cannot be represented by generated helpers. |

## Run a typed job

```csharp
internal readonly struct Integrate(float dt) : IForEach<Position, Velocity>
{
    public void Execute(ref Position position, in Velocity velocity) => position.X += velocity.X * dt;
}

world.Query<MovingEntities>().Run(new Integrate(dt));
```

`ForEachJobSourceGenerator` emits `Run(job)` and `RunParallel(job)` extensions for `QueryView`, plus `Run(job)` for `QueryCursor` and an exact-handle `world.Run(handle, job)` extension. Entity-aware interfaces route to `RunEntity`/`RunParallelEntity`. Generic jobs and jobs nested in generic containers preserve type constraints. Generated extensions require public/internal accessibility throughout the containing type chain and exclude file-local/ref-like jobs. A declaration that hides an outer generic parameter with the same name uses the runtime's explicit generic methods instead.

Job interfaces accept one writable component and up to three readonly components. If a job implements several supported interfaces, generated extensions select the greatest arity. Use explicit generic query methods to select another interface.

## Declare system access

`SystemMetadataGenerator` inspects the effective `ISystem.Update` implementation. It handles explicit interface methods, inherited source methods, generic base substitutions, and overrides. Direct `World.Read`/`Write`, resource operations, cursor/accessor `Read`/`Write`/`TryRead`, query delegates, and job calls contribute access metadata. Calls to arbitrary helper method bodies are not analyzed. Inferred types complement the explicit contract and never authorize parallel scheduling by themselves. Direct structural calls always force exclusive execution. Attributes declare the complete access contract for parallel execution.

A base implementation from another assembly has no source body to inspect. Without an explicit access declaration, its derived system is exclusive. Generic or inaccessible system declarations use runtime attributes rather than generated catalogs. Generated and runtime-only systems remain exclusive without explicit `[Reads]`/`[Writes]`/resource attributes or `[NoWorldAccess]`, including systems with known inferred accesses. Declare `[Reads]`, `[Writes]`, resource access, or `[NoWorldAccess]` when appropriate; use `[Exclusive]` for work requiring the owning thread.

Components register lazily at runtime. The old empty `ComponentCatalogGenerator` compatibility stub has been removed.

## Split fields into storage groups

`SplitFieldSourceGenerator` emits group structs and conversion helpers for accessible non-generic `[SplitFields]` components. Apply `[SplitGroup(nonNegativeId)]` to mutable public/internal fields. Readonly, fixed-buffer, pointer, inaccessible, and negative-group fields produce `BECSG004`.

Helpers preserve effective accessibility through containing types and grouped fields. Generated names encode namespace/type boundaries, generic syntax, and underscores distinctly to avoid collisions. Top-level names without special characters retain the usual `ComponentNameSplitGenerated` spelling. Generated files remain build artifacts.

Compilation tests run all four generators against valid and invalid programs, then compile their output with Roslyn. Metadata tests emit assemblies and inspect the resulting contracts, including an inherited base from a separate assembly.

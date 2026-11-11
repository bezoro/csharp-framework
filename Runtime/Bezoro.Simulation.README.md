# Bezoro.Simulation

Engine-independent simulation time and gameplay attributes. Runtime targets are .NET 10 and .NET Standard 2.1; Unity 6 consumes the precompiled .NET Standard DLL. The module references Core and has no ECS, Unity, or Godot dependency.

- [Timing](https://github.com/bezoro/csharp-framework/blob/0e73014d65f4c74f1842aac64a9be427dd3f0600/src/Bezoro.Simulation/Timing/README.md) provides fixed-step advancement and bounded integer-tick scheduling.
- [Attributes](https://github.com/bezoro/csharp-framework/blob/0e73014d65f4c74f1842aac64a9be427dd3f0600/src/Bezoro.Simulation/Attributes/README.md) provides explicit modifier formulas and reusable attribute storage.

Value models are readonly. Schedulers and attribute sets synchronize individual operations, allowing shared producers, consumers and readers without external locks. Independent instances can execute in parallel. Returned values are snapshots; application-owned buffers and reference payloads keep their own synchronization requirements. Deliver scheduled data to Unity/Godot scene objects through the engine adapter's thread rules.

Use these owners directly in a game loop or register them as ECS world resources. No global clock, background timer task, or singleton is required. The ECS-specific pipelines remain in [GameSystems](https://github.com/bezoro/csharp-framework/blob/0e73014d65f4c74f1842aac64a9be427dd3f0600/src/Bezoro.GameSystems/README.md).

```sh
dotnet build src/Bezoro.Simulation/Bezoro.Simulation.csproj -c Release
dotnet test tests/Bezoro.Simulation.Tests/Bezoro.Simulation.Tests.csproj -c Release
```

Profile allocations and execution cost using the [simulation benchmarks](https://github.com/bezoro/csharp-framework/blob/0e73014d65f4c74f1842aac64a9be427dd3f0600/benchmarks/Bezoro.Simulation.Benchmarks/README.md). A .NET build does not establish Unity Editor, IL2CPP, or Godot export compatibility; validate the selected target runtime.


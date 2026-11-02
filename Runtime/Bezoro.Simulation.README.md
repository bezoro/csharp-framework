# Bezoro.Simulation

Engine-independent simulation time and gameplay attributes. Runtime targets are .NET 10 and .NET Standard 2.1; Unity 6 consumes the precompiled .NET Standard DLL. The module references Core and has no ECS, Unity, or Godot dependency.

- [Timing](https://github.com/bezoro/framework/blob/e7472a00169f392e3724e170581fe384c0e024ef/src/Bezoro.Simulation/Timing/README.md) provides fixed-step advancement and bounded integer-tick scheduling.
- [Attributes](https://github.com/bezoro/framework/blob/e7472a00169f392e3724e170581fe384c0e024ef/src/Bezoro.Simulation/Attributes/README.md) provides explicit modifier formulas and reusable attribute storage.

Value models are readonly. Mutable owners such as schedulers and attribute sets belong to an update thread or require external serialization. Independent owners can run on separate workers. Deliver scheduled data to Unity/Godot scene objects on the engine's main thread.

Use these owners directly in a game loop or register them as ECS world resources. No global clock, background timer task, or singleton is required. The ECS-specific pipelines remain in [GameSystems](https://github.com/bezoro/framework/blob/e7472a00169f392e3724e170581fe384c0e024ef/src/Bezoro.GameSystems/README.md).

```sh
dotnet build src/Bezoro.Simulation/Bezoro.Simulation.csproj -c Release
dotnet test tests/Bezoro.Simulation.Tests/Bezoro.Simulation.Tests.csproj -c Release
```

Profile allocations and execution cost using the [simulation benchmarks](https://github.com/bezoro/framework/blob/e7472a00169f392e3724e170581fe384c0e024ef/benchmarks/Bezoro.Simulation.Benchmarks/README.md). A .NET build does not establish Unity Editor, IL2CPP, or Godot export compatibility; validate the selected target runtime.


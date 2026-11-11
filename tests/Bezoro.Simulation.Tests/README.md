# Bezoro.Simulation.Tests

Contract and regression tests for [Bezoro.Simulation](../../src/Bezoro.Simulation/README.md). Tests run without launching Unity or Godot.

```sh
dotnet test tests/Bezoro.Simulation.Tests/Bezoro.Simulation.Tests.csproj -c Release
```

See the module's documentation for ownership, capacity, and numeric contracts. These managed tests do not replace validation in engine player builds.

Concurrency cases verify complete multi-producer/multi-consumer delivery, exactly-once cancellation and slot release, relative scheduling concurrent with tick advancement, and simultaneous modifier addition/update/removal with cache refresh. Warm scheduling and attribute-operation allocation checks remain part of the suite.

# Bezoro.StateMachines.Tests

Regression and contract coverage for the [state-machine module](../../src/Bezoro.StateMachines/README.md). A dictionary model independently evaluates 100,000 seeded steps across 100 definitions. Tests cover duplicate and invalid rules, copied input, empty tables, zero-signal machines, boundary IDs, explicit self transitions, and missing rules.

One definition is queried concurrently by 32 independent agents. A warmed loop checks that pure evaluation and lookup allocate no managed bytes. Size assertions keep IDs, transition records, and step results compact. Very large declared ranges exercise sparse storage without allocating a dense table.

Builder cases execute chest transitions without numeric IDs, verify explicit initial selection and immutable snapshots, and reject default/foreign handles before changing declarations. The compiled result uses the same kernel and duplicate-rule validation as table construction.

```sh
dotnet test tests/Bezoro.StateMachines.Tests/Bezoro.StateMachines.Tests.csproj -c Release
```

The tests run on the host .NET runtime. They do not validate a Unity player, IL2CPP, or Godot export.

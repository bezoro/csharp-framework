# Unity API consumption check

This consumer targets .NET Standard 2.1 and deliberately uses C# 9. Framework projects retain `LangVersion=latest`. Its five source files reference all 24 runtime libraries and check public APIs without an engine dependency.

The solution normally builds against project references. After staging the Unity package, compile against its actual DLLs:

```sh
dotnet build samples/Bezoro.UnityApiSmoke -c Release -p:FrameworkBinaryRoot=/absolute/path/to/framework/artifacts/unity-package/Runtime
```

## Consumer coverage

| Consumer | Operations | Expected result |
| --- | --- | ---: |
| FoundationConsumer | Random, bounded collections, simulation, navigation and persistence | 4 |
| ToolkitConsumer | Inventory, spatial queries, binary spans, scalar helpers and declarative FSM construction | 4 |
| BehaviorConsumer | Conditions, parallel statecharts, trees and named action bindings with a one-visit budget | 2 |
| FrameworkConsumer | ECS Read/Write, bounded event delivery, health, typing, LogPayload construction and engine-free chess | 97 |

The console examples execute these methods. They are finite managed checks, not scene or player tests. The ECS check uses component APIs without a source generator; authoring new generated query/job types requires a compatible analyzer host separately.

`ChessProductConsumer.ExerciseDataApis()` additionally composes local authority, board feedback, study variants, a scripted lesson, quest rewards, a skin purchase and profile/rating values. Its expected result is the remaining 50 coins. Build tests execute this consumer as .NET Standard IL against the managed .NET runtime assemblies.

## Checking an older compiler

The SDK's LangVersion selects syntax, not an older compiler's metadata handling or lifetime inference. The optional helper uses a supplied compiler DLL and reference pack without downloading packages or changing the framework compiler:

```powershell
./scripts/Test-UnityApiCompiler.ps1 `
  -CompilerPath /path/to/toolset/tasks/netcoreapp3.1/bincore/csc.dll `
  -ReferenceAssemblyPath /path/to/netstandard.library.ref/2.1.0/ref/netstandard2.1
```

The helper compiles all five files with the staged runtime dependency closure. Warnings are errors except CS1701/CS1702, matching the SDK's default suppression for reference-assembly version unification. .NET Standard 2.0 dependencies legitimately bind to the supplied 2.1 profile. Other compiler warnings and errors remain fatal.

It separately compiles the two actual MonoBehaviour adapter files against the small test-only Unity API surface. This catches older compiler/source compatibility problems; it does not execute Unity lifecycle, rendering, serialization or a player build.

Real Roslyn 3.8.0-5.20562.2 checks found two important contracts. Stack-backed out-span views must first be initialized from their buffer, so the span consumers use empty views without allocating. Constructors with C# 11 required members cannot be called by that compiler, even with a complete object initializer; LogPayload now has a validated ordinary constructor instead.

The .NET 10 builds also forward the .NET Standard IsExternalInit shim to the runtime type. This keeps init-setter signatures callable when a .NET host selects the .NET framework DLLs for a consumer compiled against .NET Standard. Build tests execute that mixed-target combination, including pool options, WorldConfig, typing options, logging metadata and chess options.

The recorded check also executes the IL emitted by Roslyn 3.8 on the managed test host and verifies the results above. It does not launch Unity Editor, run IL2CPP, establish Godot export compatibility, or reproduce every engine compiler/linker configuration.

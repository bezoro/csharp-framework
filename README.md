# Bezoro Framework

Reusable C# libraries for Unity 6 and other .NET game projects, including Godot .NET. The repository uses the .NET 10 SDK and `LangVersion=latest`. Runtime libraries target `net10.0` and `netstandard2.1`; Unity consumes the precompiled .NET Standard assemblies through the `upm` branch.

## Modules

| Module | Responsibility |
| --- | --- |
| [Core](src/Bezoro.Core/README.md) | Collections, grids, pools, guards, and common utilities |
| [Events](src/Bezoro.Events/README.md) | Typed event routing, queued dispatch, and subscriptions |
| [ECS](src/Bezoro.ECS/README.md) | Worlds, component storage, queries, scheduling, and snapshots |
| [GameSystems](src/Bezoro.GameSystems/README.md) | Activation, health, input, streaming, and timers |
| [Simulation](src/Bezoro.Simulation/README.md) | Fixed-step time, tick scheduling, and attributes with modifiers |
| [Navigation](src/Bezoro.Navigation/README.md) | Weighted grid pathfinding with reusable search storage |
| [Persistence](src/Bezoro.Persistence/README.md) | Versioned save envelopes and atomic file storage |
| [Inventory](src/Bezoro.Inventory/README.md) | Bounded item stacks, atomic transfers, and validated restoration |
| [StateMachines](src/Bezoro.StateMachines/README.md) | Immutable transition tables and pure state evaluation |
| [Spatial](src/Bezoro.Spatial/README.md) | Uniform point indexing with circle and rectangle queries |
| [Conditions](src/Bezoro.Conditions/README.md) | Typed predicates and compiled short-circuit logic |
| [Statecharts](src/Bezoro.Statecharts/README.md) | Hierarchical and parallel states with event-driven transitions |
| [BehaviorTrees](src/Bezoro.BehaviorTrees/README.md) | Memory and reactive trees with bounded execution |
| [Behaviors](src/Bezoro.Behaviors/README.md) | Optional owned composition of trees, statecharts, and flat machines |
| [Logging](src/Bezoro.Logging/README.md) | Filtering, formatting, and log destinations |
| [TypingSystem](src/Bezoro.TypingSystem/README.md) | Word consumption and typing state |
| [Chess protocol and rules](src/Bezoro.Chess.UCI.Protocol/README.md) | Local chess, match state, optional UCI engines, and game review |
| [Chess game facade](src/Bezoro.Chess.UCI/README.md) | Game-facing board snapshots, moves, and UI event dispatch |
| [Chess study](src/Bezoro.Chess.Study/README.md) | SAN/PGN, retained variations, navigation and scripted lessons |
| [Chess presentation](src/Bezoro.Chess.Presentation/README.md) | Shared 2D/3D board projection, special-move feedback and configurable highlights |
| [Chess networking boundary](src/Bezoro.Chess.Networking/README.md) | Optional authoritative commands and remote engine/review service contracts |
| [Player profiles](src/Bezoro.PlayerProfiles/README.md) | Profile values, bounded match history and pure Elo updates |
| [Progression](src/Bezoro.Progression/README.md) | Event-fed quests, free/premium battle passes and story prerequisites |
| [Commerce](src/Bezoro.Commerce/README.md) | Cosmetic catalogs, currency, idempotent purchases, ownership and equipment |

The [toolkit example](samples/Bezoro.Toolkit.ConsoleDemo/README.md) combines pickup queries, inventories, quest states, and a byte checkpoint. Core also provides [binary span cursors](src/Bezoro.Core/Binary/README.md), [bit sets and span algorithms](src/Bezoro.Core/Collections/README.BitSet.md), and [scalar/grid arithmetic](src/Bezoro.Core/Numerics/README.md). See [module ownership and validation](docs/gameplay-toolkit.md).

Use the [statechart examples](samples/Bezoro.Statecharts.ConsoleDemo/README.md) for a chest, parallel character states, and scheduled perception. The [NPC example](samples/Bezoro.Behaviors.ConsoleDemo/README.md) nests trees and machines with one shared work budget. See [composition, costs and lifecycle](docs/composable-behaviors.md).

The [framework API review](docs/framework-api-review.md) describes declarative state/tree construction, ownership fixes, allocation measurements and breaking migrations across the libraries. Construction builders compile to the existing compact runtimes and can be released after setup.

For a Unity chess product, see [the integration and coverage review](docs/unity-chess-product.md). It connects local/shared-device play, Unity-managed networking, remote engines, undo, study, lesson progress, cosmetics and the same match displayed in 2D or 3D. Domain modules are optional; they impose no common update loop or backend vendor.

## Unity installation

In Unity Package Manager, choose **Install package from git URL**:

```text
https://github.com/bezoro/csharp-framework.git#upm
```

GitHub redirects the former `bezoro/framework` Git URL, so existing package references continue to resolve. Use the URL above for new installations.

Use the .NET Standard 2.1 API compatibility profile. Unity's compiler does not compile this repository's source. After updates enter `main`, the workflow builds the DLLs, validates both platforms, includes runtime dependencies, and publishes the generated UPM branch. See [package installation and platform limits](unity-package/README.md).

## Local chess without Stockfish

```csharp
using Bezoro.Chess.UCI.Protocol.API;

var match = new UciPlayableMatchSession();
var initial = match.Refresh();
match.ApplyMove("e2e4");
var current = match.Refresh();
```

Moves, legal replies, promotion, clocks, and results use local rules. No engine process or network connection is needed. Serialize mutations of a protocol match on its owning game thread. Retain immutable snapshots for readers. The high-level `UciGameEngineSession` also has a constructor without an engine path and supports local play without calling `StartAsync`.

## Optional game review

Start a dedicated UCI client only when analysis or an engine opponent is needed:

```csharp
using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Types;

await using var client = new UciEngineClient(enginePath);
await client.StartAsync(cancellationToken);
var reviewer = new UciGameReviewAnalyzer(client,
    new GameReviewOptions(new SearchParameters { Depth = 18 }));
var review = await reviewer.AnalyzeAsync(Fen.Default,
    new[] { "e2e4", "e7e5", "g1f3" }, ct: cancellationToken);
```

The reviewer evaluates the initial position and each resulting position once, reusing adjacent evaluations. It returns preferred lines, scores, expected-points loss, and move quality. Stockfish WDL estimates describe engine self-play; they are not calibrated human winning chances. Accuracy uses an explicitly documented library formula, not Chess.com's CAPS2. Rating models and special annotations are replaceable policies. See [research and review behavior](docs/chess-review.md).

## Bounded parallel work

Parallelism is explicit. Review can use a caller-owned pool of started clients with `MaxParallelPositions`, `ThreadsPerEngine`, and `HashMegabytesPerEngine`; defaults remain one worker, one engine thread, and a 16 MB hash request. Results stay chronological even when positions finish out of order. Cancellation joins every active worker before returning.

For independent ECS simulations:

```csharp
using Bezoro.ECS.Services;

var simulations = new WorldGroup(worlds, maxDegreeOfParallelism: 2);
simulations.Tick(deltaTime);
```

Each world retains its own systems, resources, and command streams. Mutable objects must not be shared between concurrent worlds. Group calls join all workers; nested scheduler work reuses the active worker rather than multiplying thread counts. A tiny workload can be slower in parallel, so profile the target and keep degree one where appropriate. Unity/Godot scene, rendering, and UI calls stay on the main thread. See [ECS threading contracts](src/Bezoro.ECS/README.md) and [review worker budgets](docs/chess-review.md).

## Gameplay foundations

The gameplay foundations also provide [deterministic random streams](src/Bezoro.Core/Random/README.md), [bounded collections and generational handles](src/Bezoro.Core/Collections/README.md), [simulation time](src/Bezoro.Simulation/Timing/README.md), [attribute calculations](src/Bezoro.Simulation/Attributes/README.md), [grid navigation](src/Bezoro.Navigation/README.md), and [save storage](src/Bezoro.Persistence/README.md). These modules operate on game data and do not call Unity or Godot APIs. Applications own the update loop and choose which independent instances to run on workers.

Run the [composed gameplay demo](samples/Bezoro.Gameplay.ConsoleDemo/README.md) to exercise navigation, modifier expiry, and checkpoint replay together. The [C# 9 API consumer](samples/Bezoro.UnityApiSmoke/README.md) checks consumption through the Unity language level. See [foundation design and validation](docs/gameplay-foundations.md) for capacity, threading, and measurement details.

## Build and contribute

```sh
dotnet restore bezoro.framework.sln
dotnet build bezoro.framework.sln -c Release --no-restore
dotnet test bezoro.framework.sln -c Release --no-build
pwsh -NoProfile -File scripts/Export-PublicApi.ps1 -Check
```

Install PowerShell 7 for build-script tests. Chess integration tests use the bundled Windows Stockfish by default. On Linux/macOS, set `BEZORO_STOCKFISH_PATH` to an absolute path to a compatible engine binary. Stockfish is not part of the published Unity package.

Pushes to working branches do not start GitHub Actions. Updates to `main`, including merges, automatically validate and publish. Run local checks during development, and dispatch **Validate and publish Unity package** only when a consolidated pre-merge check is useful. Leave `publish_upm` disabled for validation; `validation_platform` can target Windows or Linux when diagnosing a platform-specific failure. Main integration and publication always validate both platforms. A manual publication requires `main` with `publish_upm` enabled; publication is rejected on other branches.

See [contribution guidelines](CONTRIBUTING.md), [change notes](CHANGELOG.md), and the [review report](docs/framework-review.md). Runtime compatibility must also be verified in the intended Unity/Godot player build; a successful .NET build does not establish IL2CPP, trimming, WebGL, or console support.

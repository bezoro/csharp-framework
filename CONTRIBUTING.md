# Contributing

## Build contracts

Install the .NET SDK selected by `global.json` and PowerShell 7. Keep `LangVersion=latest`; do not require Unity to compile framework source. Every runtime change must compile for `net10.0` and `netstandard2.1`. The ECS source generator targets `netstandard2.0` for analyzer-host compatibility.

Run the release build and tests from the root. When a compatible Stockfish executable is not the bundled Windows binary, set `BEZORO_STOCKFISH_PATH` to its absolute path. Do not remove or silently skip engine integration coverage to get a green build.

```sh
dotnet restore bezoro.framework.sln
dotnet build bezoro.framework.sln -c Release --no-restore
pwsh -NoProfile -File scripts/Test-Framework.ps1 -NoBuild
pwsh -NoProfile -File scripts/Export-PublicApi.ps1 -Check
```

For a public-type addition, review the change and regenerate the inventory with `scripts/Export-PublicApi.ps1`. This inventory checks type declarations, not full member-level binary compatibility. Review signatures, enum numeric values, constructors, and behavioral compatibility separately.

## Code changes

Keep engine dependencies outside deterministic rules and general-purpose modules. Local chess must work without a UCI client. Explicit analysis may await a client; applying a manual move must not wait for engine search. Keep snapshots immutable and document who owns mutable state and disposable resources.

Prefer pure methods for rules, validation, classification, and numerical transformations. Use `readonly struct` or `readonly record struct` for compact immutable values and snapshots. Keep resource owners and orchestration as classes; avoid copying large value types or forcing immutable updates into component-storage hot paths that require mutation.

Prefer clear control flow and small methods with a specific responsibility. Validate public inputs before mutating shared state or writing native memory. Make disposal idempotent where promised, release pooled/native storage on exceptional exits, and test failures as well as successful calls. Do not change a failing test to preserve an implementation bug.

Use regression tests for observed defects. Add boundary, cancellation, and lifecycle cases when they exercise a meaningful contract. Use perft reference positions when changing chess generation. Measure hot paths with release builds and allocation diagnostics before introducing caches, unsafe code, or more complex representations. Report runtime, hardware, workload, and measurement limits with performance results. Avoid unlimited caches or background work that consumers did not request.

Worlds and protocol match mutations have an owning thread. Separate independent-world concurrency from concurrent mutation of the same world. UI callbacks must use the configured dispatcher; background work must not depend on a blocked UI event queue to finish.

ECS systems require explicit component/resource access declarations or NoWorldAccess before parallel scheduling. Generated inference supplements the contract; it does not prove arbitrary helper/callback effects. Copy required values out of scheduler-provided SystemContext during Update, and never retain that borrowed context for asynchronous work. Its command stream is created only on access, flushed in schedule order, then disposed after workers finish.

Update module documentation, examples, and change notes with code. Keep historical design documents historical; current READMEs describe current behavior. Clearly identify known platform limits and application-provided policies.

## Commits and versions

Use Conventional Commits, for example `fix(chess): reject moves after resignation`, `perf(core): avoid redundant allocations`, or `docs(ecs): explain snapshot ownership`. Use `!` and a `BREAKING CHANGE:` footer for incompatible API or behavior changes. Keep separate concerns in reviewable commits.

Package versions come from SemVer Git tags through `scripts/Get-PackageVersion.ps1`. Do not hand-edit release versions in generated UPM output. `BEZORO_PACKAGE_VERSION` and `-PackageVersion` are explicit local overrides. Stable release tags use `vMAJOR.MINOR.PATCH`. During the 0.x development series, release breaking changes on a new minor version and document migration steps. Preview versions inspect commits since the stable tag: breaking changes take precedence over features, which take precedence over patch changes. No release tag is implied by a development branch or an unreleased change note.

The source branch owns the build scripts and template. Updates to `main`, including merges, automatically validate both platforms and publish generated DLLs to `upm`; do not maintain a second hand-edited copy of the framework there. Pushes to working branches do not start CI. Use local checks during development. Dispatch the workflow manually only when a consolidated pre-merge check is needed; `publish_upm` can also request publication from `main`.

For a platform-specific failure, set `validation_platform` to `windows` or `linux` in a manual validation run. This avoids repeating the other runner unnecessarily. Publication requests and automatic main integration always use both platforms, regardless of the selected diagnostic target.

`validation_tests=persistence` restricts a manual diagnostic run to the persistence suite after the complete build/API checks. Main integration and publication ignore this restriction and run every suite. Use the targeted mode to investigate backend-specific failures without repeatedly running unrelated engine integration tests.

`Test-Framework.ps1` discovers every `tests/**/*.Tests.csproj`, so new modules enter local and CI validation automatically. Use `-Suite Bezoro.Inventory.Tests` to select a project by its basename, or pass multiple names. Unknown selections fail before any suite runs. `-ResultsDirectory` writes one TRX per project. Keep test project names unique and ending in `.Tests`.

For an optional older-compiler check against staged DLLs, use `scripts/Test-UnityApiCompiler.ps1` with a compiler DLL and .NET Standard 2.1 reference directory. It downloads nothing and leaves the framework's `LangVersion=latest` build unchanged. See the [consumer instructions](samples/Bezoro.UnityApiSmoke/README.md).

Stage Unity packages into a dedicated directory under `artifacts`, such as `artifacts/unity-package`. The staging script rejects source/Git paths and symbolic-link or junction ancestors before deleting previous output. Publication supplies its GitHub token through temporary process configuration, keeping credentials out of remote URLs and Git config files.

State/behavior changes must preserve explicit pay-for-use costs. Cover absence of work as well as produced results: inactive state guards must not execute, memory trees must not recheck completed prefixes, budget-one composition must advance, and canceled activations must reject late results before callbacks. Keep immutable definitions separate from per-owner runtime and document reserved capacity. Guards observe context; effects belong to actions/lifecycle handlers. Async completions must return to the owner before validation or mutation.

# Changelog

## Unreleased

### Chess undo results

- Add `UciGameEngineSession.UndoWithDetailsAsync` and `UciUndoResult` so game UIs can await the restored state and authoritative removed move payloads without correlating posted events. Existing `UndoAsync`, events, clocks and history semantics remain unchanged; both APIs share the same undo implementation and immutable move array.

### Unity package metadata

- Include stable importer metadata for every runtime XML documentation file. Staging rejects missing, malformed or duplicate asset GUIDs before publishing an immutable Git package, while preserving existing assembly GUIDs.

### Unity chess product foundations

- Optional local study trees with retained undo/redo variations, legal SAN conversion, bounded PGN import/export and authored lesson attempts with scripted replies and decision undo.
- Allocation-free board projections, configurable highlight palettes, actual capture/en-passant geometry, castling rook motion, promotion, check and mate, orientation mapping and a C# 9 Unity view adapter for shared 2D/3D state.
- Optional authoritative command boundary for Unity networking and explicit remote engine/review contracts. Includes stale-response rejection, authenticated-seat actions and opt-in casual takebacks; no network stack or backend vendor is imposed.
- Independent profiles/history/Elo, quests/battle passes/story progress, and cosmetic store modules with bounded state, explicit claims, currency and purchase receipts.
- Corrected facade clock undo, full-history engine searches, off-turn resignation/draw actions, intended-move draw claims, save restoration and playable-position validation. Resignation respects impossible mating material; draw offers survive the offerer's own move.
- Shared validated saves retain live clocks separately from undo baselines; queued analysis snapshots cannot overwrite newer UI metadata. Optional engine workers start only when used, and default coordinator options initialize valid budgets.
- Expanded precompiled Unity package, older-compiler consumption checks, cross-module smoke coverage and integration documentation. See [coverage and remaining product work](docs/unity-chess-product.md).

### Framework API and runtime review

- Optional declarative FSM/statechart builders and named tree-action bindings, with construction-only ownership handles and no extra runtime maps.
- Regression models for budget-one behavior execution, hierarchical transitions, stale leases, scheduler ordering, source generation and concurrent change tracking.
- Explicit ECS access contracts for parallelism, generation-checked cursor scopes and system contexts, guarded lifecycle transitions, and lazy command streams/buffers. Scheduler scratch is reused and no longer rebuilds conflict sets each tick.
- Removed obsolete ECS aliases, unused observers and an empty generator. Read/Write intent is explicit; temporary entity identities do not recycle, and interval accumulation remains finite at large valid inputs.
- Pool reuse without per-return node allocation, safe acquisition callbacks, corrected waiter/discard cleanup, stricter singleton ownership, direct span color formatting and checked arithmetic fixes.
- Local chess refresh/load/manual play without engine work, bounded analysis/PV retention, strict UCI command validation, promotion/parser fixes and teardown with blocked stdin.
- Event budgets on the core interfaces, smaller queued-event allocation, reliable phase-bus cleanup, coherent typing values and correct async logging scope/severity behavior.
- Atomic health requests, finite double-precision streaming calculations, bounded input ingestion, callback release, timer transition preservation and streaming access limited to its selected window.
- C# 9-callable LogPayload construction and init-modifier type forwarding across .NET Standard/.NET runtime builds. Four managed consumers cover every runtime library; mixed-target execution has its own regression tests.
- Protected literal staging paths and temporary Git publication authentication without tokens in URLs. Updated migration guides, examples, public-type inventories and measured allocation limits. See [the review report](docs/framework-api-review.md).

### Composable behaviors

- Independent typed Conditions, Statecharts, BehaviorTrees and optional Behaviors integration assemblies.
- Hierarchical and orthogonal state regions, External/Local/Internal transitions, pure pre-dispatch guards, and sparse active-source event routing.
- Memory and reactive trees, subtree occurrences, bounded continuations, compact compiled tables and runtime storage based on active path depth.
- Shared visit budgets across nested trees and machines, explicit terminal mappings, bounded completion signals, exclusive child ownership, and cleanup on interruption or completion.
- Activation liveness covers pending exits and ancestor scopes, rejecting stale synchronous and asynchronous completion signals.
- Statechart and composed NPC examples, scheduled perception through the existing TickScheduler, and expanded C# 9/older-compiler API checks.

### Gameplay toolkit

- Independent inventory, state-machine, and spatial assemblies with no engine dependencies.
- Immutable item catalogs and bounded stacks, all-or-nothing inventory transfers, and validated save restoration.
- Shared sparse transition tables with pure state evaluation and explicit self-transition behavior.
- Bounded uniform point grids, stable generational handles, rectangle/circle filtering, and caller-owned query buffers.
- Fixed-capacity bit sets, sorted span bounds, and span removal that releases reference payloads.
- Little-endian binary span readers/writers with per-field atomic failure and overlapping source support.
- Signed floor/ceiling division, Euclidean modulo, finite scalar interpolation, wrapping, movement, and elapsed-time damping.
- Fixed integer magnitude overflow, negative/overflowing nearest-multiple rounding, and exponential damping at floating-point extremes.
- Composed pickup/quest/inventory checkpoint example, expanded C# 9 DLL consumption checks, and an optional older-compiler check. Stack-backed output views also compile under Roslyn 3.8.
- Test-suite discovery shared by local validation and main-only CI; new modules no longer require a hard-coded test list.

### Gameplay foundations

- Deterministic PCG32 state, unbiased integer ranges, portable floating-point draws, span shuffling, and immutable integer-weight selection tables.
- Fixed-capacity generational slot maps and FIFO ring buffers with explicit rejection or overwrite semantics and cleared references.
- Independent `Bezoro.Simulation` assembly with pure fixed-step time, bounded tick scheduling, finite attribute formulas, generational modifier handles, and cached per-attribute evaluation.
- Independent `Bezoro.Navigation` assembly with copied immutable weighted grids, reusable A* storage, integer costs, cancellation, expansion budgets, and caller-provided path buffers.
- Independent `Bezoro.Persistence` assembly with a versioned binary envelope, metadata/payload CRC32, bounded decoding, application-owned schemas, and atomic local file replacement.
- Snapshot opens, save commits, and deletes use fixed shared coordination across same-process store instances to prevent concurrent Windows replacement/open failures; payload I/O remains outside the gates. Deletion retires the active name by rename so open snapshots cannot block immediate slot reuse.
- Composed gameplay/checkpoint console example and a .NET Standard 2.1 API consumer intentionally compiled with C# 9. Framework source remains on `LangVersion=latest`.
- New module DLLs and stable Unity metadata in UPM, cross-platform test coverage, and locally recorded benchmark evidence.
- CI runs automatically on updates to `main`, including merges. Working-branch pushes do not start CI; manual consolidated validation remains available. Generated UPM commits now use Conventional Commits.

### API and behavior changes

- Runtime projects target .NET 10 and .NET Standard 2.1. Tests, samples, and benchmarks use .NET 10. The compiler language setting remains `latest`.
- The UPM package targets Unity 6 and still contains precompiled DLLs. It includes the required NuGet runtime dependency closure and upstream license notice.
- Manual chess gameplay is local. `UciGameEngineSession` accepts construction without an engine path; loading, moves, promotion, and undo do not wait for Stockfish. Automatic background analysis requires `EnableBackgroundAnalysis=true`.
- Missing evaluations remain absent rather than being represented as equal positions. Mate predictions no longer imply immediate checkmate.
- UCI node, node-rate, tablebase-hit, and elapsed-time counters and related constructor/deconstruction members use `ulong`.
- Grid constructors, coordinates, and disposed access reject invalid usage. Event buses reject registration after disposal. Code relying on permissive or incorrect behavior must handle these failures.

### Chess

- Strict structural FEN parsing without regular expressions or token-array allocation, with rank and counter validation.
- Legal king safety, en passant, castling, promotion, and repetition identity fixes; no king captures or phantom en passant.
- Engine-free protocol sessions, synchronous refresh, incremental repetition counts, and reuse of resulting-position legal moves.
- Terminal-result enforcement, clock freeze, fivefold repetition and 75-move automatic draws, timeout precedence, atomic setup validation, history-aware undo, and stale-analysis rejection.
- Dedicated `UciGameReviewAnalyzer`, immutable review results, rating-model and annotation policies, WDL, expected-points quality, finite budgets, cancellation, and progress. The documented accuracy formula is library-specific, not CAPS2.
- UCI search and transport lifecycle serialization, waiter/timer cleanup, cumulative counters, final primary-variation selection, and stop/go ordering.
- Explicit search stop waits for regular-search cleanup before returning, so an immediate replacement search can acquire the active slot.
- Public application-transport injection and asynchronous stdout/stderr reads without retaining idle pool threads.
- Context-aware analysis caching retains the base FEN and complete move history.
- Review supports an explicit bounded pool of dedicated clients, per-engine CPU/hash requests, chronological results, monotonic progress, and cancellation/failure cleanup.
- Local facade construction allocates no engine runtime. State streams retain only the latest pending immutable snapshot and reject subscription races after disposal.
- Engine-backed sessions prune completed background and ponder-stop tasks even when automatic analysis is disabled, while retaining unfinished work for disposal.

### Other modules

- Core grids, pool capacity and waiter lifecycle, duplicate returns, swapback collection boundaries, aliased enumeration, and null guards.
- Event-subscription identity and disposal synchronization.
- ECS native bounds and layout handling, restored-resource lifetime, isolated-world thread caches, failed creation/shutdown cleanup, command-stream ownership, and finite deltas.
- Opt-in ECS WorldGroup execution across independent worlds, cooperative nested workers, immutable membership, and profiled sequential/parallel workloads.
- Typing completion, single enumeration of word sources, and removal of consumed words without losing unread content.
- Logging filters run before formatting.
- Timer input validation and precise long-running input timestamps.

### Validation and documentation

- Regression tests across every changed runtime module, reference perft coverage, Stockfish integration, and cross-platform build-script tests.
- Linux integration engine selection through `BEZORO_STOCKFISH_PATH` and Linux/Windows CI validation.
- Package dependency conflict detection, missing-artifact failures, protected staging paths, stable dependency metadata, and XML documentation.
- SemVer numeric/ordinal prerelease precedence, numeric-identifier validation, large version components, build metadata preservation, and preview bumps based on Conventional Commit feature/breaking intent.
- Updated module guides, root setup, contribution rules, architecture research, and review evidence. No release tag is created by this unreleased entry.

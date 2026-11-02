# Unity chess product review

The framework can supply the deterministic game and study domain for a complete Unity chess product. It does not supply a finished Chess.com competitor: scenes, artwork, user interfaces, authored teaching content and deployed account/backend services are application work. The modules below have separate ownership and no mandatory global update loop.

| Product concern | Framework support | Application responsibility |
| --- | --- | --- |
| Two humans on one device | Engine-free `UciPlayableMatchSession` or `UciGameEngineSession` | Input, promotion panel, presentation and local settings |
| Two humans over a network | Optional `AuthoritativeChessMatch` command validation and immutable snapshots | Unity transport/RPC, authenticated connection-to-seat assignment, reconnection, hosting and matchmaking |
| Remote computer opponent | `IChessMoveService`, captured-revision validation, finite search requests, server-side UCI adapter | Remote transport, server quotas, deployment and engine strength policy |
| Rules and results | Legal local generation, castling, en passant, underpromotion, check/mate/stalemate, repetition and move-count draws, clocks | Ruleset/product policy and exceptional adjudication described below |
| Undo/takebacks | Retained board/clock checkpoints, explicit save restoration, optional consensual network takebacks | Ranked/casual policy and authority over requests |
| Study | `ChessStudy`, undo/redo and variations, SAN, PGN, FEN starts, comments and NAGs | Study storage, collections and analysis UI |
| Engine review | Explicit `UciGameReviewAnalyzer` or `IChessReviewService` request | Server budget, ratings calibration, opening data, explanation/coach content |
| Progressive lessons | Validated `ChessLesson` solutions, scripted replies, feedback, hints, decision undo | Authored exercises, localized explanations, target pedagogy and narrative |
| Profile and archive | Immutable profiles and match metadata, bounded duplicate-aware history | Authentication, durable archive/PGN storage and pagination |
| Ranking | Pure configurable Elo pair updates | Rating pools, provisional/season policy, leaderboard database and anti-abuse |
| Quests/battle pass/story | Indexed event counters, explicit claims, XP tracks and prerequisite graph | Event authorization/deduplication, content catalogs, season lifecycle and durable reward transactions |
| Skin store | Catalog/offer windows, exact currency arithmetic, idempotent purchases/grants, ownership and equipment | Catalog rollout, asset delivery, payments if any, durable transactions |
| 2D/3D board and feedback | Immutable board projection, configurable overlays, capture/castle/promotion geometry and paired asset identifiers | Sprite/prefab assets, shaders, animation, accessibility and cameras |

## One match, multiple views

Create one session per match. The presentation module derives an immutable projection when the state or selection changes. Both renderers consume that projection; switching cameras, skin asset variants or orientation does not recreate the session, send a move or restart clocks. The projection includes the actual captured square for en passant, the rook's two castling squares, promotion destinations and the checked/mated king. Renderers decide overlay blending and animation, and may use each highlight independently instead of the palette's precedence.

The [Unity adapter sample](../samples/Bezoro.Chess.Unity/README.md) has an abstract renderer and an event-driven MonoBehaviour presenter. It contains no per-frame board scan. Input emits intent; the local controller or networking owner validates it before presenting the accepted snapshot. The sample's C# 9 source is compiled against a small test-only Unity API stub, with adapter contract tests. That checks managed API/source integration, not real Editor lifecycle or rendering behavior. Run the target Unity 6 project and player builds before shipping.

Keep rendering, scene and UI operations on Unity's main thread. Independent immutable snapshots and catalog definitions may be shared with workers. Mutable match, study, account and progression owners require their documented serialization; adding a thread to a cheap local chess operation would add scheduling cost with no required benefit. Engine service awaits and optional review pools are the useful concurrency boundaries.

## Undo, replay and save boundaries

Live undo rewinds rule state and clock checkpoints. It must restore the active side, castling rights, en-passant target, halfmove/fullmove counters, repetition context and any adjudicated result. A clocked save needs historical clock checkpoints to permit takebacks before the load point: a final clock pair alone cannot reconstruct previous increments, stages and time spent. Use protocol `CaptureSave`/`LoadSaveAsync` or facade `CaptureSaveAsync`/`LoadSaveAsync` when that history must survive restart. The APIs share the same validated save contract, with the saved current clock kept separate from turn-start undo baselines. A FEN alone is insufficient for repetition or a complete match archive.

Players may resign even outside their turn. Local `Resign(side)`/`ResignAsync(side)` and the authenticated network resignation command finalize the result, freeze clocks and invalidate pending engine work. Saves retain that result. FIDE's impossible-mate exception uses the conservative material test described below.

Study undo has different semantics: it moves the cursor while preserving all continuations, and a new move becomes a sibling variation. Study nodes retain their full positions so navigation does not replay the line. Scripted lesson undo rewinds one learner decision together with its automatic opponent reply, preserving mistake counts until reset. None of these operations requires an engine.

Network takebacks are disabled unless the host opts in. Human-vs-human proposals require consent; casual engine takebacks require host authorization. Revision checks invalidate engine responses computed before a move, takeback or result change. The networking boundary supplies no sockets, replication system, matchmaking or automatic retry service; use the project's native Unity networking facilities.

## Chess.com comparison and rule limits

The public [analysis-board documentation](https://support.chess.com/en/articles/8583825-how-do-i-use-the-analysis-board) includes FEN/PGN loading, move exploration, variations, engine lines, configurable board feedback and cloud analysis. Those are useful domain/integration targets; the framework now exposes the corresponding local study and remote-service boundaries.

Chess.com's [move-classification documentation](https://support.chess.com/en/articles/8572705-how-are-moves-classified-what-is-a-blunder-or-brilliant-etc) describes rating-dependent expected points and loss thresholds. It does not publish its full rating calibration or special-label implementation. Its [CAPS2 accuracy description](https://support.chess.com/en/articles/8708970-how-is-accuracy-in-analysis-determined) is not a reproducible formula. This library's Stockfish-WDL model and documented accuracy formula therefore remain explicitly distinct. Do not label its outputs as identical Chess.com accuracy, human win probability or official Chess.com rankings. Opening-book labels, calibrated human models, sacrifice heuristics and coach explanations require additional data/policies; see [review research and contracts](chess-review.md).

[FIDE Laws of Chess](https://handbook.fide.com/chapter/E012023) distinguish claimable threefold/fifty-move draws from automatic fivefold/seventy-five-move draws; a checkmating last move takes precedence. The local rules cover standard legal moves and these history-dependent results. Impossible-mate detection is conservative material analysis. It does **not** prove every possible blocked dead position or every asymmetric position where no legal sequence can mate. This affects automatic dead-position, timeout and resignation adjudication in those unusual positions. An exhaustive arbitrary-position proof is not claimed; neither a shallow engine evaluation nor a material imbalance alone resolves that rule. Over-the-board arbiter procedures, touch-move and tournament penalties are outside a software move generator.

## Authority, persistence and costs

Only the host authorizes rated results, XP, premium access, currency and cosmetic grants. Quest counters deliberately do not keep a universal event log: repeated delivery counts twice unless the host deduplicates event identity. Commerce retains bounded successful transaction receipts and rejects new mutations when full; it never evicts identities and accidentally permits a replay. Persist progression changes and reward delivery in one durable transaction or outbox workflow before acknowledging a claim. No in-memory module supplies a distributed database transaction.

Profile history is a bounded working set, not the entire account database. Store replay files separately and retain their reference in match metadata. Share immutable content catalogs; create mutable owners only for the features used. Modules create no polling loops. PGN import has input/token/node/nesting budgets; optional match/review histories have explicit limits. Snapshots and requested history copies allocate intentionally; cheap projection/query paths are checked separately.

## Unity compatibility and validation

Keep runtime binaries on `netstandard2.1`, built with `LangVersion=latest`; Unity does not compile the framework source. Unity 6's [API compatibility documentation](https://docs.unity3d.com/6000.0/Documentation/Manual/dotnet-profile-support.html) supports .NET Standard libraries and describes AOT differences. The .NET 10 build serves managed hosts and servers; it is not the DLL selected for Unity.

Real Roslyn 3.8/C# 9 consumption, mixed-target managed execution and .NET tests establish useful API and IL checks. They do not establish IL2CPP, trimming, WebGL, device or console behavior. The process-based UCI transport is for supported desktops/servers; remote service contracts avoid requiring an installed Stockfish executable in mobile/WebGL clients. Measure gameplay latency and allocations in the actual target player, independently from engine search time.

### Recorded local verification

Runtime changes through `db8fc78b` were checked with .NET SDK 10.0.203/runtime 10.0.7 and Stockfish 19 on Linux. The consolidated result is **3,663 passed tests across 25 suites**, with zero failures or skipped cases. This includes 773 protocol/rules tests, 285 facade tests, 35 networking tests, 24 presentation tests, 40 study tests and 37 build/compatibility tests; the other framework suites remain covered as well.

- Both runtime targets built with zero warnings/errors, using the canonical package version `0.1.0-preview.1667` for this recorded build.
- Staging produced 31 runtime DLLs and 59 unique Unity GUIDs. Public-type inventory checks passed.
- Real Roslyn 3.8 compiled all five C# 9 consumers and the two actual Unity adapter files against the test-only Unity surface. Executing its generated consumer IL on the managed host returned the expected `4, 4, 2, 97, 50`; four existing console examples also completed.
- Six reference perft positions totaled 500,117 leaves. Independent python-chess 1.11.2 comparisons found no mismatch across 5,593 rule positions (48 deterministic games, seed 113589) and 1,498 SAN vectors.
- Checked-arithmetic runs passed for progression, commerce and study. Model tests cover event counters, claim replay and restoration; queued-callback tests cover moves, takebacks, terminal results, clock changes and draw offers. Completion notifications retain exactly-once delivery for their current generation and obtain the latest payload, including changes made between typed and canonical callbacks.
- Warmed .NET 10 checks observed zero managed allocation for unchanged protocol refreshes with history, board projection/queries, and the documented progression query/update paths. Snapshot/save creation, changed history and explicit serialization still allocate. These checks are not Unity-player latency or allocation guarantees.

The recorded package version identifies this verification build, not a fixed installation requirement. Publication resolves the merge revision's version through `Get-PackageVersion.ps1`. Real Unity/Godot player testing, deployed networking/backend services, art and authored curriculum remain outside these managed checks.

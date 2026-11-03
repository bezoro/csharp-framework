# Bezoro.Chess.UCI.Protocol
Low-level UCI transport, parsing, and engine-client orchestration for chess engines.

## Types
| Type                         | Namespace                             | Description                                                                                               |
|------------------------------|---------------------------------------|-----------------------------------------------------------------------------------------------------------|
| `UciEngineClient`            | `Bezoro.Chess.UCI.Protocol.API`       | Main async client for handshake, search orchestration, typed protocol events, and raw engine interaction. |
| `ProcessUciTransport`        | `Bezoro.Chess.UCI.Protocol.API`       | Process-backed stdin/stdout/stderr transport for a UCI engine executable.                                 |
| `SearchParameters`           | `Bezoro.Chess.UCI.Protocol.API.Types` | Standard UCI `go` parameters with explicit validation.                                                    |
| `SearchResult`               | `Bezoro.Chess.UCI.Protocol.API.Types` | Parsed result of a completed search.                                                                      |
| `UciClientOptions`           | `Bezoro.Chess.UCI.Protocol.API.Types` | Client-level timeout and protocol-behavior configuration.                                                 |
| `ChessSquare`                | `Bezoro.Chess.UCI.Protocol.API.Types` | Zero-based board-square value with algebraic parsing and formatting helpers.                              |
| `UciCoordinateMove`          | `Bezoro.Chess.UCI.Protocol.API.Types` | Strict parser and formatter for UCI coordinate moves, including promotion suffix validation.               |
| `FenPiecePlacementParser`    | `Bezoro.Chess.UCI.Protocol.API.Common.Helpers` | FEN piece-placement parser for board renderers and setup tools that need occupied-square maps. |
| `PositionScore`              | `Bezoro.Chess.UCI.Protocol.API.Types` | Player-relative centipawn or mate score with compact display/sort helpers.                                |
| `PositionAdvantage`          | `Bezoro.Chess.UCI.Protocol.API.Types` | Normalized player-relative advantage summary for lightweight UIs.                                         |
| `MoveClassificationFlags`    | `Bezoro.Chess.UCI.Protocol.API.Types` | Structural and tactical flags such as capture, promotion, check, mate, and stalemate.                    |
| `MoveClassification`         | `Bezoro.Chess.UCI.Protocol.API.Types` | Fully typed move metadata including moving piece, captured piece, promotion piece, and resolved flags.    |
| `MoveEvaluation`             | `Bezoro.Chess.UCI.Protocol.API.Types` | Absolute player-relative score for a legal move candidate's resulting position plus classification data.  |
| `MatchSideControllerKind`    | `Bezoro.Chess.UCI.Protocol.API.Types` | Per-side control mode for playable matches: manual or engine-driven.                                      |
| `PlayableMatchClaimableDrawPolicy` | `Bezoro.Chess.UCI.Protocol.API.Types` | Controls whether repetition and fifty-move draws are automatic or must be explicitly claimed.        |
| `PlayableMatchDrawOfferPolicy` | `Bezoro.Chess.UCI.Protocol.API.Types` | Controls whether pending draw offers expire on the recipient's next move or persist until answered.                  |
| `PlayableMatchControlledMoveFallbackPolicy` | `Bezoro.Chess.UCI.Protocol.API.Types` | Controls whether engine-controlled turns fall back to a local legal move or throw on search failure. |
| `PlayableMatchTimeoutPolicy` | `Bezoro.Chess.UCI.Protocol.API.Types` | Controls whether flag fall is automatic loss or ignored by the protocol session.                          |
| `PlayableMatchTimeControl`   | `Bezoro.Chess.UCI.Protocol.API.Types` | Symmetric clock configuration with increment, delay, timeout policy, and optional staged controls.        |
| `PlayableMatchTimeControlStage` | `Bezoro.Chess.UCI.Protocol.API.Types` | Additional staged time-control segment triggered after a per-side move count.                          |
| `PlayableMatchClockState`    | `Bezoro.Chess.UCI.Protocol.API.Types` | Snapshot of both clocks for the current turn.                                                             |
| `PlayableMatchClockRestore`  | `Bezoro.Chess.UCI.Protocol.API.Types` | Authored clock snapshot used to restore arbitrary remaining time while loading a match position.          |
| `PlayableMatchClockSetup`    | `Bezoro.Chess.UCI.Protocol.API.Types` | Authored clock values for match setup loading; active side, move counts, and stage can be derived.       |
| `PlayableMatchSetup`         | `Bezoro.Chess.UCI.Protocol.API.Types` | Complete authored or saved match state: base FEN, played moves, and optional clock setup.                |
| `FenPositionSetup`, `FenPositionValidationResult` | `Bezoro.Chess.UCI.Protocol.API.Types` | Editable position authoring, FEN construction/import, field diagnostics, and validated history setup. |
| `PlayableMatchSave`          | `Bezoro.Chess.UCI.Protocol.API.Types` | Complete local save with available clock history, result, draw offer and pending promotion, preserving undo after reload. |
| `PlayableMatchResult`        | `Bezoro.Chess.UCI.Protocol.API.Types` | Adjudicated terminal result with winner information when decisive.                                        |
| `PlayableMatchResultReason`  | `Bezoro.Chess.UCI.Protocol.API.Types` | Terminal reason such as checkmate, stalemate, repetition, fifty-move rule, insufficient material, or timeout. |
| `PendingPromotionRequest`    | `Bezoro.Chess.UCI.Protocol.API.Types` | Promotion request/response payload for moves that still need a promotion piece.                           |
| `PlayableMatchMoveData`      | `Bezoro.Chess.UCI.Protocol.API.Types` | Canonical applied-move payload including from/to, pieces, classification, and previous/resulting FEN.    |
| `PlayableMatchPiece`         | `Bezoro.Chess.UCI.Protocol.API.Types` | Lightweight piece payload for move events with side, normalized kind, and original symbol.               |
| `PlayableMatchSecondaryMove` | `Bezoro.Chess.UCI.Protocol.API.Types` | Secondary piece motion payload used for castling rook movement.                                           |
| `PlayableMatchEvent`         | `Bezoro.Chess.UCI.Protocol.API.Types` | Canonical protocol-side event payload emitted by a playable match session.                                |
| `PlayableMatchEventKind`     | `Bezoro.Chess.UCI.Protocol.API.Types` | Event discriminator for `PlayableMatchEvent`.                                                             |
| `PlayableMatchRequest`       | `Bezoro.Chess.UCI.Protocol.API.Types` | Serializable request DTO for server-authoritative or transport-driven playable sessions.                  |
| `PlayableMatchRequestKind`   | `Bezoro.Chess.UCI.Protocol.API.Types` | Request discriminator for `PlayableMatchRequest`.                                                         |
| `PlayableMatchCommand`       | `Bezoro.Chess.UCI.Protocol.API.Types` | Parsed textual command for playable-match workflows such as UCI moves, history, and FEN loading.         |
| `PlayableMatchCommandKind`   | `Bezoro.Chess.UCI.Protocol.API.Types` | Command discriminator for `PlayableMatchCommand`.                                                          |
| `PlayedMove`                 | `Bezoro.Chess.UCI.Protocol.API.Types` | Chronological played-move record including parent/result positions and best-known classification.         |
| `PlayableMatchState`         | `Bezoro.Chess.UCI.Protocol.API.Types` | Current playable-match snapshot including position, legal moves, move classifications, advantage, and history. |
| `EngineMoveResult`           | `Bezoro.Chess.UCI.Protocol.API.Types` | Engine move and search result produced during a playable match turn.                                      |
| `MoveAnalysisResult`         | `Bezoro.Chess.UCI.Protocol.API.Types` | Immutable snapshot of analyzed legal moves for a position.                                                |
| `PositionAnalysisResult`     | `Bezoro.Chess.UCI.Protocol.API.Types` | Shared current-position advantage and legal-move analysis snapshot from the same search flow.             |
| `UciProtocolMessage`         | `Bezoro.Chess.UCI.Protocol.API.Types` | Immutable envelope for parsed protocol output with typed optional payload fields keyed by `Type`.         |
| `UciInfoMessage`             | `Bezoro.Chess.UCI.Protocol.API.Types` | Typed `info ...` payload including score, depth, PV, refutation, and current line data.                   |
| `UciBestMoveMessage`         | `Bezoro.Chess.UCI.Protocol.API.Types` | Typed `bestmove ...` payload.                                                                             |
| `UciIdMessage`               | `Bezoro.Chess.UCI.Protocol.API.Types` | Typed `id name` / `id author` payload.                                                                    |
| `UciOptionMessage`           | `Bezoro.Chess.UCI.Protocol.API.Types` | Typed `option name ...` payload.                                                                          |
| `UciEngineOption`            | `Bezoro.Chess.UCI.Protocol.API.Types` | Parsed engine option metadata in advertised order.                                                        |
| `UciEngineCapabilities`      | `Bezoro.Chess.UCI.Protocol.API.Types` | Observed and probed capability state for standard commands and extensions.                                |
| `UciRegistration`            | `Bezoro.Chess.UCI.Protocol.API.Types` | Payload for the standard `register` command.                                                              |
| `EngineActivity`             | `Bezoro.Chess.UCI.Protocol.API.Types` | Coarse engine activity state.                                                                             |
| `TransportStatus`            | `Bezoro.Chess.UCI.Protocol.API.Types` | Transport lifecycle state.                                                                                |
| `UciPositionAnalysisCoordinator` | `Bezoro.Chess.UCI.Protocol.API`   | FIFO full-strength position-analysis queue with completed-result caching by position key.                 |
| `UciPlayableMatchSession`    | `Bezoro.Chess.UCI.Protocol.API`       | Stateful playable-match coordinator with manual/manual, manual/engine, or engine/engine side control. |

## Game review types

| Type | Responsibility |
| --- | --- |
| `UciGameReviewAnalyzer` | Serialized game reviews with an optional bounded pool of dedicated engine clients |
| `GameReviewOptions` | Finite search, CPU, hash-table, and history budgets with optional player ratings |
| `GameReviewResult`, `GameReviewEvaluation`, `ReviewedMove` | Immutable review, position, and played-move results |
| `GameReviewProgress` | Monotonic completed/total counts and chronological position index, including parallel reviews |
| `UciWdl` | Validated win/draw/loss distribution and expected points |
| `MoveQuality`, `MoveQualityClassifier` | Pure expected-points-loss classification |
| `IExpectedPointsModel`, `EngineWdlExpectedPointsModel` | Replaceable probability model and engine-self-play default |
| `MoveReviewAnnotation`, `IMoveReviewAnnotationPolicy` | Application-provided opening and special-label evidence |

See the [review architecture and research](https://github.com/bezoro/framework/blob/a517911092a882b4d8b6efada0488da063272c56/docs/chess-review.md) for the public Chess.com behavior studied, the implemented thresholds, and the proprietary features that are not reproduced.

## Local play without an engine

Use the local constructor for Unity or Godot gameplay. It creates no process or transport and does not start background analysis. Moves, legal-move generation, promotion, clocks, and results run locally.

```csharp
await using var match = new UciPlayableMatchSession();
await match.StartNewGameAsync(); // Completes synchronously for a local session.
var opening = match.Refresh();
match.ApplyMove("e2e4");
var reply = match.Refresh();
```

`Refresh()` is synchronous in every session. `RefreshAsync()` is its awaitable counterpart. Loading, refreshing, and applying manual moves contact no engine and start no analysis, including when engine clients are configured. `GetLegalMoveAnalysisAsync()` explicitly queues and awaits Stockfish analysis; it requires an analysis engine. `WaitForCurrentMoveClassificationsAsync()` explicitly requests local tactical batch classification. Neither operation runs during ordinary refreshes.

Serialize mutations to a session. Immutable returned snapshots may be retained by UI code. An asynchronous engine result is rejected if a load, move, undo, or result change made its position obsolete.

Threefold repetition and the fifty-move rule follow the configured claim policy. Fivefold repetition and the seventy-five-move rule end the game automatically; checkmate takes priority. Terminal results freeze the clock until a new setup or undo. Timeout is a draw if the opponent cannot possibly mate by material, with a conservative material test rather than a solver for all blocked dead positions. Additional clock-stage triggers must be strictly increasing.

`CanClaimDraw(intendedMove)` and `ClaimDraw(intendedMove)` support declaring a legal move that would complete a third repetition or the fifty-move limit before executing it (FIDE articles 9.2.1.1 and 9.3.1). A successful claim leaves the board and move history unchanged and stops the clock without awarding an increment. The query inspects board/history availability; the command also adjudicates the live clock. A `ClaimDraw` request can carry the intended UCI move through its `Move` field.

Use `OfferDraw(side)`, `AcceptDraw(side)`, `DeclineDraw(side)` and `Resign(side)` when the actor may differ from the side to move, as in an authenticated multiplayer server. Only the recipient can accept or decline an offer. Under `ExpireOnMove`, an offer survives the offering side's own move and expires when its recipient moves. Resignation draws if the opponent cannot possibly mate by the same conservative material test (FIDE article 5.1.2). `Fen.IsInCheck()` exposes allocation-free local check detection for presentation code.

`CancelPendingPromotion()` dismisses an uncompleted promotion without undoing a committed move, stopping the clock or granting increment. It returns whether a pending choice was dismissed and emits `PromotionCanceled`. Expired clocks and other terminal results dismiss promotion automatically. `Fen.Parse` checks notation syntax; `Fen.ValidateForPlay()` checks basic position legality for gameplay. Loading a match validates its base position and rejects move histories that continue beyond an automatic result. Position validation is not a proof of full historical reachability. These contracts follow the [FIDE Laws of Chess](https://handbook.fide.com/chapter/E012023); physical tournament procedures and arbiters remain outside the local game API.

Clock timestamps use the supplied `utcNowProvider`, with `DateTimeOffset.UtcNow` as the default. The kernel clamps negative elapsed time to zero; wall-clock changes such as NTP adjustments can still change the charged time. Applications needing a monotonic clock should provide timestamps derived from a fixed UTC origin plus `Stopwatch` elapsed time.

For analysis from an authored position, pass its base FEN and complete move history to the explicit-base overload of `UciPositionAnalysisCoordinator.Enqueue`. The older overload retains its standard-initial-position behavior. Reusing a key with a different base position, history, perspective, or legal-move list invalidates the previous cache entry and cancels its outstanding waiters. Retired requests cannot overwrite the replacement analysis. Engines that advertise the boolean `UCI_ShowWDL` option have it enabled for analysis.

## Quick Start
```csharp
using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Types;

var options = new UciClientOptions
{
    UciHandshakeTimeout = TimeSpan.FromSeconds(5),
    ReadyTimeout = TimeSpan.FromSeconds(10),
    DefaultSearchTimeout = TimeSpan.FromSeconds(30)
};

await using var client = new UciEngineClient(enginePath, options: options);
await client.StartAsync(cancellationToken);

await client.SetPositionAsync(Fen.Default, null, cancellationToken);

var result = await client.GoAsync(
    new SearchParameters { Depth = 12 },
    cancellationToken);

Console.WriteLine($"Best move: {result.BestMove}");
Console.WriteLine($"Ponder:    {result.PonderMove}");
Console.WriteLine($"Eval cp:   {result.BestCpScore}");
```

## Typed Protocol Stream
```csharp
using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Types;

await using var client = new UciEngineClient(enginePath);

client.InfoReceived += message =>
{
    var payload = message.Payload;
    if (payload.Depth is not uint depth || payload.PrincipalVariation is not { } pv)
        return;

    Console.WriteLine($"depth {depth}: {pv.RawPv}");
};

client.BestMoveMessageReceived += message =>
    Console.WriteLine($"bestmove {message.BestMove} ponder {message.PonderMove}");

client.ProtocolMessageReceived += message =>
    Console.WriteLine($"{message.Type}: {message.RawLine}");

client.StderrReceived += line =>
    Console.Error.WriteLine($"stderr: {line}");

await client.StartAsync(cancellationToken);
await client.SetPositionAsync(Fen.Default, null, cancellationToken);
await client.GoFireAndForgetAsync(new SearchParameters { Infinite = true }, cancellationToken);

await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
await client.StopSearchAsync(cancellationToken);
await client.IsReadyAsync(cancellationToken);
```

`InfoPvReceived` remains a supported compatibility event for principal-variation consumers. The older event aliases remain available but are obsolete: `BestMoveReceived` projects `UciBestMoveMessage.BestMove` and `PonderMove` into two string arguments and reports "Use BestMoveMessageReceived instead.", while `LineReceived` reports "Use RawLineReceived instead." New code should use the canonical events.

Typed `info` messages, `PrincipalVariation.TryParse`, and `SearchResult.TryParse` use one shared UCI info grammar. Outer whitespace and repeated ASCII spaces are accepted; tabs are not token separators. The `info` prefix, `cp`/`mate` score kinds, and score bounds are case-insensitive, while field keywords such as `depth` and `pv` retain their lowercase UCI spelling. Move text keeps the engine's original casing, and `string` and `pv` consume the remainder of their line.

Collection-bearing protocol snapshots use immutable storage:
- `SearchParameters.SearchMoves`
- `SearchResult.PrincipalVariations`
- `PrincipalVariation.Moves`
- `UciInfoPayload.Refutation` / `CurrentLine`
- `UciEngineOption.Variables`
- `UciEngineClient.AvailableOptions`

## Option Discovery And Validation
```csharp
using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Types;

await using var client = new UciEngineClient(enginePath);
await client.StartAsync(cancellationToken);

Console.WriteLine($"{client.EngineInfo.Name} by {client.EngineInfo.Author}");

if (client.TryGetOption("Threads", out var threads))
{
    Console.WriteLine($"Threads default: {threads.DefaultValue}");
    await client.SetOptionAsync(threads.Name, "4", cancellationToken);
}

foreach (var option in client.AvailableOptions)
{
    Console.WriteLine($"{option.Name} [{option.Type}] default={option.DefaultValue}");
}

if (client.TryGetStrengthLimitRange(out int minElo, out int maxElo))
{
    int targetElo = Math.Clamp(1500, minElo, maxElo);
    await client.SetStrengthLimitAsync(targetElo, cancellationToken);
}
```

`SearchParameters` now requires at least one explicit search limit or mode such as `Depth`, `Nodes`, `MoveTimeMs`, `WhiteTimeMs`/`BlackTimeMs`, `Mate`, `Infinite`, or `Ponder`. Empty `go` requests are rejected instead of silently defaulting to a fallback search.

`SetOptionAsync` rejects blank option names with `ArgumentException` instead of silently no-oping.

`TryGetStrengthLimitRange` and `SetStrengthLimitAsync` are small ergonomic helpers over the standard `UCI_LimitStrength` / `UCI_Elo` option pair. They keep raw option-name plumbing out of application code while still respecting the engine's advertised metadata.

## Engine-Specific Escape Hatches
```csharp
using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Types;

await using var client = new UciEngineClient(enginePath);
await client.StartAsync(cancellationToken);
await client.SetPositionAsync(Fen.Default, ["e2e4", "e7e5"], cancellationToken);

if (client.Capabilities.SupportsCoordinatorExtensions)
{
    Fen? currentFen = await client.TryGetFenViaDisplayBoardAsync(cancellationToken);
    var legalMoves = await client.GetLegalMovesViaPerftAsync(cancellationToken);
}
```

`TryGetFenViaDisplayBoardAsync` and `GetLegalMovesViaPerftAsync` are deliberate non-standard helpers. They remain available because higher layers in this repo depend on them, but they should be treated as engine-specific extensions rather than standard UCI behavior.

## Move Classification Helpers
```csharp
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;

Fen fen = Fen.Parse("7k/5Q2/7K/8/8/8/8/8 w - - 0 1")!.Value;

MoveClassification structuralOnly = fen.ClassifyMove("f7g7");
MoveClassification full = fen.ClassifyMoveFully("f7g7");

Console.WriteLine(structuralOnly.IsResolved); // false
Console.WriteLine(full.IsCheck);              // true
Console.WriteLine(full.IsMate);               // true
```

`ClassifyMove` and `ClassifyMoves` are the zero-search structural fast path. `ClassifyMoveFully` and `ClassifyMovesFully` resolve check, mate, and stalemate locally from FEN plus UCI move notation, without engine round-trips.

## Coordinate And Placement Helpers
Use `UciCoordinateMove` when a caller needs strict UCI coordinate parsing with square values and promotion-travel validation:
```csharp
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;

if (" A7A8Q ".TryNormalizeCoordinateMove(out string moveText) &&
    UciCoordinateMove.TryParse(moveText, out var move))
{
    ChessSquare from = move.From;
    ChessSquare to = move.To;
    char? promotion = move.PromotionPiece;
}
```

`NormalizeUciMoves` remains a lightweight collection normalizer. It intentionally does not reject existing loose inputs. Use the strict helpers when validation matters.

Board renderers and setup tools can parse occupied squares from either full FEN text or a placement field:
```csharp
using Bezoro.Chess.UCI.Protocol.API.Common.Helpers;
using Bezoro.Chess.UCI.Protocol.API.Types;

var pieces = new Dictionary<ChessSquare, char>();
bool parsed = FenPiecePlacementParser.TryParse(Fen.Default.Raw, pieces);
```

Debug-display helpers are also available for simple text UIs:
```csharp
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;

string evaluationText = evaluation.ToDebugDisplayString();
string historySuffix = playedMove.Classification.ToDebugSuffix();
```

`PlayedMoveHistoryExtensions.ToDisplayLines` now includes those classification suffixes automatically when the played moves carry them.

## Playable Match Command Parsing
```csharp
using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Types;

PlayableMatchCommand command = PlayableMatchCommandParser.Parse(
    "loadfen rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1 moves e2e4 e7e5");

if (command.Kind == PlayableMatchCommandKind.LoadFen)
{
    Fen fen = command.Fen!.Value;
    var moves = command.Moves;
}
```

This parser is intended for terminal apps or debug tooling built on top of `UciPlayableMatchSession`. It recognizes UCI moves, `moves`, `history`, `undo`, `quit`, and `loadfen <fen> [moves ...]`.

## Analysis Helpers
```csharp
using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;

await using var client = new UciEngineClient(enginePath);
await client.StartAsync(cancellationToken);
await client.SetPositionAsync(Fen.Default, ["e2e4", "e7e5"], cancellationToken);

Fen currentFen = (await client.TryGetFenViaDisplayBoardAsync(cancellationToken))!.Value;
var legalMoves = (await client.GetLegalMovesViaPerftAsync(cancellationToken)).NormalizeUciMoves();
var advantage = await client.EvaluateAdvantageAsync(currentFen.ActiveColor, 'w', ct: cancellationToken);
var moveEvaluations = await client.AnalyzeLegalMovesAsync(
    currentFen.ActiveColor,
    playerColor: 'w',
    legalMoves,
    ct: cancellationToken);

var positionAnalysis = await client.AnalyzePositionAsync(
    currentFen.ActiveColor,
    playerColor: 'w',
    legalMoves,
    ct: cancellationToken);
```

`EvaluateAdvantageAsync` and `AnalyzeLegalMovesAsync` package the score-normalization, MultiPV probing, fallback single-move evaluation, and display-friendly absolute move-score logic that previously only lived in the console sample. Their centipawn values are player-relative current evaluations; they are not offset against a start-position calibration.

`AnalyzePositionAsync` and `UciPositionAnalysisCoordinator` are intended for non-blocking UIs that want the current advantage bar and legal-move list to come from the same full-strength analysis stream. The coordinator processes accepted positions in FIFO order. Its constructor accepts `maxCachedPositions` and `maxPendingPositions`, both 128 by default. Completed results use oldest-completed eviction. A full pending budget rejects a new key before altering accepted work. Replacing a queued key releases its obsolete history. Canceling one `GetAnalysisAsync(key, token)` wait leaves shared analysis running; `Cancel()` cancels shared work and clears results. `Dispose()` cancels and permanently closes the coordinator; `DisposeAsync()` also joins the worker before its caller-owned engine can be released.

## Playable Match Workflow
```csharp
using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Types;

await using var playingClient = new UciEngineClient(enginePath);
await using var analysisClient = new UciEngineClient(enginePath);

await Task.WhenAll(
    playingClient.StartAsync(cancellationToken),
    analysisClient.StartAsync(cancellationToken));

await using var session = new UciPlayableMatchSession(
    playingClient,
    analysisClient,
    perspectiveColor: 'w',
    whiteController: MatchSideControllerKind.Manual,
    blackController: MatchSideControllerKind.Engine);

await session.StartNewGameAsync(cancellationToken);

PlayableMatchState state = await session.RefreshAsync(cancellationToken);
var openingAnalysis = await session.GetLegalMoveAnalysisAsync(cancellationToken);
var legalMoveClassifications = await session.WaitForCurrentMoveClassificationsAsync(cancellationToken);

session.ApplyMove("e2e4");
state = await session.RefreshAsync(cancellationToken);

if (session.GetController(state.Fen.ActiveColor) == MatchSideControllerKind.Engine)
{
    EngineMoveResult engineMove = await session.PlayControlledMoveAsync(cancellationToken);
    state = await session.RefreshAsync(cancellationToken);
}

await session.WaitForCurrentMoveClassificationsAsync(cancellationToken);
string[] historyLines = session.GetMoveHistoryDisplayLines();
PositionAdvantage liveAdvantage = session.ResolveCurrentAdvantage();

if (session.CanUndoMoves())
{
    session.UndoMoves();
    state = await session.RefreshAsync(cancellationToken);
}
```

Editors can author an incomplete board without implementing FEN compression or chess validation:

```csharp
var position = FenPositionSetup.FromFen(Fen.Default);
position.SetPiece(new ChessSquare(0, 1), '\0');
Fen authoredFen = position.BuildFen();
FenPositionValidationResult validation = position.ValidateForPlay();
PlayableMatchSetup playable = position.CreateMatchSetup();
```

`BuildFen` checks notation syntax and preserves the authored metadata. It does not repair counters or require a playable position. `ValidateForPlay` returns friendly field errors separately from the framework's underlying position rejection. `CreateMatchSetup` rejects invalid positions. Its optional history base is authoritative and validates both that base and every played move, instead of treating the editable board as a second history snapshot.

`PlayableMatchTimeControl.FromClampedSeconds` and `PlayableMatchClockSetup.FromClampedSeconds` normalize authored negative seconds to zero and validate the result. Time-control initial time must remain positive. Negative infinity clamps to zero; NaN is rejected and positive overflow throws. Their regular constructors preserve supplied values for callers that want explicit `Validate` calls without normalization.

For authored tutorial positions, save states, and editor tooling, load a complete setup object instead of passing FEN, moves, and clocks through separate parameters:
```csharp
var setup = new PlayableMatchSetup(
    Fen.Default,
    ["e2e4", "e7e5"],
    new PlayableMatchClockSetup(
        whiteRemaining: TimeSpan.FromMinutes(3),
        blackRemaining: TimeSpan.FromMinutes(2),
        isPaused: true));

PlayableMatchState loaded = await session.LoadMatchAsync(setup, cancellationToken);
EngineMoveResult? reply = await session.PlayControlledMoveIfNeededAsync(cancellationToken);
```

`UciPlayableMatchSession` keeps the sample's reusable match orchestration in the library: local legal-move generation and FEN ownership, move-history tracking, controller-driven automatic engine turns, request/response promotion flow, draw and timeout adjudication, optional clocks, serializable request processing, canonical protocol-side events, and current advantage resolution from the same full-strength move evaluations used for move lists and debugging history. Applied moves include structural, check, mate and stalemate classifications immediately. Classifying every possible move is a separate explicit batch request.

The classifier publishes completed tactical results as one immutable batch per position. After cancellation, replacement classification waits for the retiring classifier to exit before it starts.

`UndoMoves` restores the retained checkpoint's clock values, move counts, and stage, then restarts that position's turn unpaused at the undo time with its full current-stage delay. For a clocked authored setup, undo is available back to the loaded position; earlier played moves have no clock checkpoints to restore and cannot be undone through this session.

Use `CaptureSave()` and `LoadSaveAsync(save)` to retain available undo checkpoints across a reload:

```csharp
PlayableMatchSave save = session.CaptureSave();
// Persist the immutable value using the application's serializer/storage.
await restoredSession.LoadSaveAsync(save, ct: cancellationToken);
restoredSession.UndoMoves();
PlayableMatchState priorPosition = restoredSession.Refresh();
```

The receiving session must have the same time-control configuration and draw policies. `Setup.Clock` holds the exact live clock; every `ClockHistory` entry holds its original turn-start baseline. Keeping these separate preserves both remaining time on resume and correct undo after a subsequently played move. Loading validates clock entries, board-derived results and winners, and any declared draw move before replacing the current game. The default resumes unpaused clocks at load time, preserving saved remaining time and partially consumed delay. Pass `countTimeSinceSave: true` for a running server clock that must include elapsed time since the snapshot. Paused and terminal clocks remain paused. Saves include the base FEN, full move list, available clock history and its undo boundary, result, declared draw move, draw offer and pending promotion. They exclude engines, transports, caches and UI state. Snapshot arrays are created only when saving; ordinary gameplay retains no additional save structure. A save is application-owned data, not an authenticated network command or proof that a resignation/agreement was authorized.

Unchanged refreshes and repeated `MoveHistory` reads reuse an immutable history snapshot. New moves, undo, loading and changed classifications invalidate that snapshot; previously returned arrays remain immutable. Engine-free refreshes reuse the parsed current FEN and perform check/material queries on the stack. Allocation-free steady-state refresh is verified on .NET 10; Unity Mono/IL2CPP performance must be measured in the consuming project.

Insufficient-material adjudication recognizes kings only, a lone knight, and bishop-only positions where every bishop remains on the same square-color complex.

`EventOccurred` is intentionally transport-friendly: every event carries `SchemaVersion`, the resulting `PlayableMatchState` when available, and a canonical `MoveData` payload for applied moves so UI or server consumers do not need to reverse-engineer captures, promotion pieces, or castling rook movement from raw UCI strings.

The event ordering contract is stable and tested for the important flows:
- promotion: `PromotionRequired` -> `PromotionChosen` -> `MoveApplied`
- terminal applied move: `MoveApplied` -> `ResultChanged`
- batch processing: events are returned in the same order they were emitted

`ProcessAsync` supports request-driven orchestration for local tools or server-authoritative play, including:
- `StartNewGame`
- `LoadPosition`
- `Refresh`
- `ApplyMove`
- `ChoosePromotion`
- `PlayControlledMove`
- `UndoMoves`
- `OfferDraw` / `AcceptDraw` / `DeclineDraw` / `ClaimDraw`
- `Resign`
- `PauseClock` / `ResumeClock`
- `CancelAnalysis`

For server-oriented consumers, `ProcessBatchAsync(requests, ct)` applies a sequence of `PlayableMatchRequest` values and returns the ordered `PlayableMatchEvent` values emitted while doing so.

## Text Formatting Helpers
```csharp
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;

string[] boardLines = fen.ToDisplayLines(playerColor: 'w', legalMoveCount: legalMoves.Length);
string[] advantageLines = advantage.ToDisplayBarLines();
string engineLine = result.ToDisplayString();
string playerEngineLine = result.ToPlayerDisplayString(sideToMove: 'b', playerColor: 'w');
```

These helpers are intentionally lightweight. They are suitable for samples, diagnostics, or simple terminal UIs without forcing consumers into a richer board-rendering abstraction.

## API Reference
### `UciPlayableMatchSession`
| Member                           | Description                                                                 |
|----------------------------------|-----------------------------------------------------------------------------|
| `StartNewGameAsync(ct)`          | Clears local state and resets clocks, promotion and result without contacting an engine. |
| `LoadMatchAsync(setup, ct)`      | Loads a complete `PlayableMatchSetup`, refreshes state, restores optional clock values, and returns the loaded state. |
| `CaptureSave()` / `LoadSaveAsync(save, countTimeSinceSave, ct)` | Captures/restores local match metadata and the available clock history, preserving undo after reload. |
| `LoadPositionAsync(baseFen, moves, ct)` | Obsolete migration shim for older callers; prefer `LoadMatchAsync`. |
| `RefreshAsync(ct)`               | Rebuilds the current local FEN, legal moves, result, clock snapshot, move history, and live non-blocking advantage. |
| `GetLegalMoveAnalysisAsync(ct)`  | Awaits the full-strength move analysis for the current position.           |
| `GetCurrentLegalMoveClassifications()` | Returns the latest cached move-type map for the current position.    |
| `WaitForCurrentMoveClassificationsAsync(ct)` | Awaits completion of background check/mate/stalemate resolution for the current position. |
| `TryGetLegalMoveClassification(move, out classification)` | Reads a cached move classification for the current position. |
| `ApplyMove(move)`                | Validates and applies a move for the current manually controlled side, or raises a pending promotion request when a promotion suffix is still needed. |
| `ApplyHumanMove(move)`           | Obsolete compatibility alias. Migration message: "Use ApplyMove instead." |
| `ChoosePromotion(piece)`         | Completes the current pending promotion using `q`, `r`, `b`, or `n`.       |
| `CancelPendingPromotion()`       | Dismisses an uncompleted promotion, leaving the board, move history and running clock unchanged. |
| `PlayControlledMoveAsync(ct)`    | Plays the current side's move when that side is engine-controlled.         |
| `PlayControlledMoveIfNeededAsync(ct)` | Plays only when the current side is engine-controlled and the match is still playable; otherwise returns `null`. |
| `PlayEngineMoveAsync(ct)`        | Obsolete compatibility alias. Migration message: "Use PlayControlledMoveAsync instead." |
| `PlayUntilTerminalAsync(maxPlies, ct)` | Plays engine-controlled turns in sequence until the game is terminal or the ply cap is reached. |
| `CanUndoMoves(count)`            | Reports whether the requested number of moves and any required retained clock checkpoint are available. |
| `UndoMoves(count)`               | Rewinds played moves, preserves reachable caches and retained clock values, and restarts the retained turn unpaused at the undo time; clocked authored setups cannot rewind before their loaded checkpoint. |
| `CanClaimDraw(move)` / `ClaimDraw(move)` | Inspects or claims a third repetition/fifty-move draw by a legal intended move without executing it. |
| `OfferDraw(side)` / `AcceptDraw(side)` / `DeclineDraw(side)` / `Resign(side)` | Identifies the actor explicitly for either side, including off-turn actions. |
| `ProcessAsync(request, ct)`      | Applies a serializable `PlayableMatchRequest` against the session.         |
| `ProcessBatchAsync(requests, ct)` | Applies a request batch and returns the ordered emitted events for that batch. |
| `TryGetPlayedMoveScore(move, out score)` | Resolves a played move back to its parent-position high-quality score. |
| `TryGetPlayedMoveClassification(move, out classification)` | Resolves the latest known classification for a played move. |
| `TryGetPositionAnalysis(key, out analysis)` | Reads a completed cached position analysis when available.           |
| `ResolveCurrentAdvantage()`      | Resolves the best completed current advantage without blocking gameplay.    |
| `GetMoveHistoryDisplayLines()`   | Builds simple debugging lines for played-move history.                     |
| `CancelAnalysis()`               | Cancels in-flight full-strength analysis and local classification.                                  |
| `PendingPromotion`               | Current pending promotion request, when a move still needs a promotion choice. |
| `EventOccurred`                  | Canonical versioned event stream with ordered match events and rich applied-move payloads. |
| `PerspectiveColor`               | Side used for board-orientation and player-relative evaluation helpers.    |
| `WhiteController` / `BlackController` | Per-side controller kinds (`Manual` or `Engine`).                    |
| `GetController(side)`            | Returns the configured controller for `w` or `b`.                          |
| `PlayerColor` / `EngineColor`    | Compatibility helpers for single-human/single-engine sessions only.        |
| `PlayedMoves` / `MoveHistory`    | Current raw played moves and structured played-move history.               |
| `BaseFen`                       | Initial position from which the complete move history begins. |
| `CurrentState`                   | Latest refreshed match snapshot including optional pending promotion, claimable draw state, clock, and adjudicated result. |

Constructor policies:
- `claimableDrawPolicy`: automatic vs explicit claim for repetition and fifty-move draws
- `drawOfferPolicy`: expire on the recipient's move vs persist until explicit response
- `controlledMoveFallbackPolicy`: local fallback vs throw when an engine turn cannot produce a valid `bestmove`

### `UciEngineClient`
| Member                                       | Description                                                                      |
|----------------------------------------------|----------------------------------------------------------------------------------|
| `StartAsync(ct)`                             | Starts the engine process, read loop, and performs UCI handshake.                |
| `StopAsync(ct)`                              | Stops the client and underlying transport gracefully.                            |
| `UciInitAsync(ct)`                           | Sends `uci` and waits for `uciok` and `readyok`. Usually called by `StartAsync`. |
| `IsReadyAsync(ct)`                           | Sends `isready` and waits for `readyok`.                                         |
| `UciNewGameAsync(ct)`                        | Sends `ucinewgame` and waits for readiness.                                      |
| `SetOptionAsync(name, value, ct)`            | Sends `setoption`; completes after `readyok`; throws on blank names.             |
| `SetDebugAsync(enabled, ct)`                 | Sends `debug on` or `debug off`.                                                 |
| `RegisterAsync(registration, ct)`            | Sends the standard `register` command.                                           |
| `PonderHitAsync(ct)`                         | Sends `ponderhit`.                                                               |
| `SetPositionAsync(fen, moves, ct)`           | Sends `position startpos ...` or `position fen ...`.                             |
| `GoAsync(parameters, ct)`                    | Runs a bounded search and returns a parsed `SearchResult`.                       |
| `GoFireAndForgetAsync(parameters, ct)`       | Starts a search without waiting for `bestmove`.                                  |
| `StopSearchAsync(ct)`                        | Sends `stop`.                                                                    |
| `TryGetFenViaDisplayBoardAsync(ct)`          | Requests current FEN using the non-standard `d` command.                         |
| `GetLegalMovesViaPerftAsync(ct)`             | Requests legal moves using the non-standard `go perft 1` listing.                |
| `BuildGoCommand(parameters)`                 | Utility for building a validated raw UCI `go` command string.                    |
| `TryGetOption(name, out option)`             | Looks up an advertised option with case-insensitive matching.                    |
| `TryGetStrengthLimitRange(out min, out max)` | Reads the advertised Elo range for engines that support `UCI_LimitStrength`.     |
| `SetStrengthLimitAsync(elo, ct)`             | Enables `UCI_LimitStrength` and sets `UCI_Elo` with range-aware validation.      |
| `EngineInfo`                                 | Engine name and author parsed during handshake.                                  |
| `AvailableOptions`                           | Options advertised during handshake, preserved in advertised order.              |
| `Capabilities`                               | Standard and extension capability state discovered so far.                       |
| `Options`                                    | Client-level timeout configuration.                                              |
| `InfoReceived`                               | Event for parsed `UciInfoMessage` output.                                        |
| `BestMoveMessageReceived`                    | Event for parsed `UciBestMoveMessage` output.                                    |
| `ProtocolMessageReceived`                    | Event for all parsed protocol messages.                                          |
| `RawLineReceived`                            | Event for every raw stdout line.                                                 |
| `InfoPvReceived`                             | Supported compatibility event for principal-variation-bearing info output.       |
| `BestMoveReceived`                           | Obsolete string projection of `UciBestMoveMessage.BestMove` and `PonderMove`. Migration message: "Use BestMoveMessageReceived instead." |
| `LineReceived`                               | Obsolete event alias. Migration message: "Use RawLineReceived instead."         |
| `StderrReceived`                             | Event for redirected stderr lines.                                               |
| `ActivityChanged`                            | Event for `Idle`/`Searching`/`Pondering` transitions.                            |

### `ProcessUciTransport`
| Member                                              | Description                                        |
|-----------------------------------------------------|----------------------------------------------------|
| `ReadLinesAsync(ct)`                                | Async line stream from engine stdout.              |
| `WriteLineAsync(line, ct)`                          | Writes a command line to stdin.                    |
| `TryWriteLineAsync(line, timeout, ct)`              | Timeout-bounded write attempt.                     |
| `StartAsync(ct)`                                    | Launches the engine process and background loops.  |
| `StopAsync(ct)`                                     | Stops loops and tears down the process gracefully. |
| `Status`                                            | Current lifecycle state.                           |
| `IsStarted`                                         | Whether the transport has been started.            |
| `IsHealthy`                                         | Best-effort health check for process and loops.    |
| `LinesRead` / `LinesWritten` / `BackpressureEvents` | Transport counters useful during diagnostics.      |

### `SearchParameters`
| Property                                | Meaning                                                                |
|-----------------------------------------|------------------------------------------------------------------------|
| `Depth`                                 | Search to a fixed depth. Must be greater than zero when set.           |
| `Nodes`                                 | Search a fixed number of nodes. Must be greater than zero when set.    |
| `MoveTimeMs`                            | Search for a fixed amount of time. Must be greater than zero when set. |
| `WhiteTimeMs` / `BlackTimeMs`           | Remaining clock time. Must be greater than zero when set.              |
| `WhiteIncrementMs` / `BlackIncrementMs` | Clock increments. Must not be negative.                                |
| `MovesToGo`                             | Number of moves until the next time control. Must not be negative.     |
| `Mate`                                  | Search for a mate in N. Must be greater than zero when set.            |
| `SearchMoves`                           | Restrict search to specific moves. Stored as an immutable snapshot.    |
| `Infinite`                              | Search until explicitly stopped.                                       |
| `Ponder`                                | Start a ponder search.                                                 |

### `UciProtocolMessageType`
| Value            | Meaning                                |
|------------------|----------------------------------------|
| `Id`             | Parsed `id name` / `id author` output. |
| `Option`         | Parsed `option name ...` output.       |
| `UciOk`          | Parsed `uciok` output.                 |
| `ReadyOk`        | Parsed `readyok` output.               |
| `Info`           | Parsed `info ...` output.              |
| `BestMove`       | Parsed `bestmove ...` output.          |
| `CopyProtection` | Parsed `copyprotection ...` output.    |
| `Registration`   | Parsed `registration ...` output.      |

## Sample
See `samples/Bezoro.Chess.UCI.Protocol.ConsoleDemo` for an interactive playable console sample. It prompts for engine Elo and player color, renders the board from protocol-owned FEN state, validates human moves against protocol-owned legal moves, and lets the engine answer with timed searches. The `moves` command, `history` command, and current cp bar all resolve from the same full-strength move evaluations, with positions analyzed in move order instead of skipping ahead to the latest position. Move types such as capture, en passant, promotion, castling, check, mate, and stalemate are resolved through the protocol library and carried through both legal-move lists and played-move history. The sample also supports `loadfen <fen> [moves ...]` for targeted debugging of specific positions and edge cases.

## Benchmarks
```bash
dotnet run -c Release --project benchmarks/Bezoro.Chess.UCI.Protocol.Benchmarks/Bezoro.Chess.UCI.Protocol.Benchmarks.csproj
```

The protocol benchmark project covers both UCI info parsing and the local-rules hot paths that back playable-match orchestration: legal move generation and full move classification.

## Design Notes
- This project owns transport lifecycle, line dispatch, command serialization, handshake parsing, typed protocol messages, and safe async protocol behavior.
- The protocol layer stays engine-agnostic for standard UCI behavior.
- Extension probing exists here only as an explicit low-level escape hatch for engines that support them; playable-match orchestration no longer depends on those non-standard commands.
- Local legal-move generation, move application, and tactical classification share one parsed position; batch classification parses its position at most once.
- `SetOptionAsync` waits for `readyok`, which makes option updates safe to compose in application code.

## Stockfish win/draw/loss estimates

Enable Stockfish's `UCI_ShowWDL` option to receive estimates alongside centipawn and mate scores:

```csharp
await client.SetOptionAsync("UCI_ShowWDL", "true", cancellationToken);
var result = await client.GoAsync(new SearchParameters { Depth = 16 }, cancellationToken);
if (result.BestPv is { Wdl: { } wdl } pv && pv.ScoreBound == UciScoreBound.Exact)
{
    double expectedPoints = wdl.ExpectedPoints;
    double opponentExpectedPoints = wdl.OpponentPerspective.ExpectedPoints;
}
```

`UciWdl` stores wins, draws, and losses per thousand games. Its constructor requires values in `0..1000` whose sum is `1000`. `ExpectedPoints` counts a draw as half a point. The estimate is relative to the side to move in the searched position. When evaluating a resulting position for the player who just moved, use `OpponentPerspective`.

The parser preserves WDL in `UciInfoPayload.Wdl` and `PrincipalVariation.Wdl`. Malformed distributions remain absent. `PrincipalVariation.ScoreBound` distinguishes exact scores from lower and upper search bounds. A bound must not be presented as an exact evaluation of move quality. These are engine self-play estimates, not an Elo-specific human prediction or a reproduction of Chess.com's private review algorithm. See the [Stockfish FAQ](https://official-stockfish.github.io/docs/stockfish-wiki/Stockfish-FAQ.html).

## Search completion and restart

`GoAsync` and `GoFireAndForgetAsync` share one active-search slot. Overlapping searches throw `InvalidOperationException`. Fire-and-forget searches forward live events without retaining an unbounded PV history. `StopSearchAsync` waits for the engine's `bestmove` response and the stopped search's cleanup before returning. After a successful stop, a new search can begin immediately without separately awaiting the original `GoAsync` task. A `readyok` response alone does not acknowledge that a search has stopped.

After cancellation or timeout, the client sends `stop` and drains its response within `SearchStopGracePeriod`. If the engine never acknowledges with `bestmove`, subsequent searches require `StopAsync` followed by `StartAsync`. UCI has no request identifiers, so reusing that connection could attribute an old best move to a new position.

Search node counts, tablebase hits, and elapsed time are cumulative engine counters. `SearchResult` keeps their highest reported values, including `info` messages without a PV. `BestPv` and `BestCpScore` use the deepest primary variation and prefer the most recent report at equal depth. Engine terminal responses `bestmove 0000` and `bestmove (none)` complete normally.

Lifecycle operations serialize startup, shutdown, and disposal. Canceling the token originally passed to a successful `StartAsync` does not stop the reader; call `StopAsync` to end the client lifetime.

UCI node counts, nodes per second, tablebase hits, and elapsed milliseconds use `ulong` in `UciInfoPayload`, `PrincipalVariation`, and search totals. Counters above `uint.MaxValue` remain available without truncation. Depth and MultiPV indices remain `uint`. This changes constructor and deconstruction signatures; consumers with explicit `uint` variables must use `ulong`. Search budgets keep their existing protocol types.

### Score absence and display

`PositionScore.FromEngineScore` preserves an absent evaluation as nullable values; `ToDisplayString()` returns `unknown`. A genuine zero-centipawn score remains distinct. Invalid sides and unrepresentable sign inversion are rejected. Mate ordering reserves values outside the complete centipawn range and handles extreme input without integer overflow. Mate zero displays as `M0`; its sign cannot encode a winner, so use the match or review terminal result.

### Application-provided transports

`new UciEngineClient(transport, options)` accepts an `IUciTransport` implementation supplied by the application. The client owns that transport's start/stop/disposal lifecycle. This allows native engine bridges and remote analysis without requiring `Process.Start`. The adapter must preserve ordered UCI lines, cancellation, and the advertised threading/lifecycle contracts; no native, WebGL, or networking adapter is bundled.

### Parallel game review

`UciGameReviewAnalyzer` accepts a pool of distinct, already started dedicated clients. Set `GameReviewOptions.MaxParallelPositions` to opt in; the default remains one. `ThreadsPerEngine`, `HashMegabytesPerEngine`, and `MaxGameMoves` provide explicit CPU, hash-table, and history budgets. Results remain chronological and immutable. Progress carries `PositionIndex` because searches can finish out of order. See [parallel review and resource budgets](https://github.com/bezoro/framework/blob/a517911092a882b4d8b6efada0488da063272c56/docs/chess-review.md#parallel-review-and-resource-budgets) for ownership, cancellation, and engine configuration.

## API migration

The engine-backed match constructor now takes two distinct caller-owned clients: `playingClient` for explicitly controlled turns and `analysisClient` for explicitly requested legal-move analysis. Remove the previous unused middle client and pass the previous move-list client as `analysisClient`. The budget parameters are now `analysisMoveTimeMs` and `analysisFallbackMoveTimeMs`. No client starts during construction, loading, or refreshing.

Refresh no longer queues Stockfish work. Call `GetLegalMoveAnalysisAsync()` for scores and `WaitForCurrentMoveClassificationsAsync()` for tactical classifications. Retained snapshots stay immutable. Dispose the match with `await using` before releasing its clients so pending analysis has finished canceling. A disposed match rejects commands. The clients remain caller-owned.

`UciPositionAnalysisCoordinator.Dispose()` is terminal. Use `Cancel()` to clear and reuse a coordinator. Its cache and pending queue have finite configurable limits. `UciClientOptions.MaxCapturedPrincipalVariations` bounds recent PV snapshots retained by each awaited search, default 4096. Live events still receive every line, and `SearchResult.TryParse` preserves the complete caller-supplied transcript. Fire-and-forget searches retain no PV history.

Malformed entries in position histories and `SearchMoves` now reject the whole command instead of silently dropping moves. Typed option and registration fields reject CR, LF, and NUL before transport writes. Search subsets therefore cannot silently become unrestricted searches. Process teardown starts the bounded exit or kill path while joining blocked stdin writes, allowing children that stopped reading to be released.


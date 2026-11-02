# Bezoro.Chess.UCI
High-level game-engine event layer built on `Bezoro.Chess.UCI.Protocol`.

## Types
| Type                             | Namespace                           | Description                                                                                    |
|----------------------------------|-------------------------------------|------------------------------------------------------------------------------------------------|
| `UciGameEngineSession`           | `Bezoro.Chess.UCI.API`              | Preferred game-engine-facing facade for synchronized position state, search updates, move classification, and UI events. |
| `UciCoordinatorOptions`          | `Bezoro.Chess.UCI.API.Types`        | Tuning for ponder threads, MultiPV, classification depth, engine move time, draw policy, and clocks. |
| `UciState`                       | `Bezoro.Chess.UCI.API.Types`        | Immutable snapshot of board, search, move-classification, result, draw-offer, and clock state. |
| `Move`                           | `Bezoro.Chess.UCI.API.Types`        | Classified move plus semantic UI-facing analysis.                                              |
| `MoveAnalysis`                   | `Bezoro.Chess.UCI.API.Types`        | Flags such as capture, castling, check, mate, promotion, and stalemate.                        |
| `BoardState`                     | `Bezoro.Chess.UCI.API.Types`        | Board model derived from FEN for move classification logic.                                    |
| `ParsedMove`                     | `Bezoro.Chess.UCI.API.Types`        | Parsed move notation with source/target squares and promotion info.                            |
| `MoveScore`                      | `Bezoro.Chess.UCI.API.Types`        | Evaluation score for a move in centipawns or mate distance.                                    |
| `Piece`, `Position`, `Promotion` | `Bezoro.Chess.UCI.API.Types`        | Chess-semantic types used by classification and UI layers.                                     |
| `GameMoveEvent`                  | `Bezoro.Chess.UCI.API.Types`        | Rich canonical move payload for UI-facing gameplay events.                                     |
| `PromotionRequiredEvent`         | `Bezoro.Chess.UCI.API.Types`        | Pending-promotion payload for request/response UI flows.                                       |
| `PromotionChosenEvent`           | `Bezoro.Chess.UCI.API.Types`        | Promotion-resolution payload emitted before the move is applied.                               |
| `MoveUndoneEvent`                | `Bezoro.Chess.UCI.API.Types`        | Undo payload carrying removed moves and the resulting position.                                |
| `TurnChangedEvent`               | `Bezoro.Chess.UCI.API.Types`        | Active-side transition payload for board and HUD updates.                                      |
| `PositionLoadedEvent`            | `Bezoro.Chess.UCI.API.Types`        | Explicit load payload for save/load, replay, or editor workflows.                              |
| `UciGameEngineSessionEvent` / `UciGameEngineSessionEventKind` | `Bezoro.Chess.UCI.API.Types` | Compact typed event stream payload and kind enum for session integrations. |
| `PieceColor`, `PieceType`        | `Bezoro.Chess.UCI.API.Common.Enums` | Chess enums used by move and board analysis.                                                   |
| `GameMoveActor`, `GameMoveKindFlags` | `Bezoro.Chess.UCI.API.Common.Enums` | UI-facing enums describing move initiators and structural move kinds.                        |

## Local gameplay

The constructor without an engine path creates no UCI clients or process transports. `NewGameAsync`, `LoadMatchAsync`, `ResetAsync`, `MakeMoveAsync`, promotion, and undo use local rules. `NewGameAsync` immediately restores twenty legal opening moves and fresh running clocks. They do not start or wait for Stockfish. The async signatures remain for callers that already await them. With an available command gate, local operations complete synchronously.

```csharp
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.Protocol.API.Types;

await using var session = new UciGameEngineSession(
    syncContext: SynchronizationContext.Current);
await session.LoadMatchAsync(PlayableMatchSetup.Standard, cancellationToken);

session.MoveMade += move => AnimateMove(move.From, move.To);
session.PositionChanged += state => DrawBoard(state.CurrentFen);
await session.MakeMoveAsync("e2e4", cancellationToken);
await session.UndoAsync(ct: cancellationToken);
```

Capture, castling, promotion, check, mate, and stalemate come from local board rules. An absent engine score stays absent. A predicted forced mate in an engine variation does not mark an ordinary move as immediate checkmate.

`CancelPendingPromotionAsync(requestId)` dismisses a matching promotion prompt while leaving the board and running clock in place, and publishes `PromotionCanceled`. A stale request identifier cannot cancel a newer prompt.

`UndoAsync` restores the retained position's clock checkpoint, including remaining times, increments already awarded, completed-move counts, and time-control stages. The retained turn restarts unpaused at the undo time. A loaded match containing only a current clock snapshot has no historical clock values: undo cannot cross that load point.

`CaptureSaveAsync` / `LoadSaveAsync` preserve all available clock checkpoints for later takebacks, plus the current clock debit, pending promotion, draw offer, adjudicated result, and any declared draw-claim move. `PlayableMatchSave` is shared with `UciPlayableMatchSession.CaptureSave` / `LoadSaveAsync`; either session API can consume the other's save. Both use the same save validator and pure result adjudicator. The receiving session must use equal clock and draw policies. Loading resumes at load time by default; opt into `countTimeSinceSave` to charge time spent offline. Engines and analysis caches are excluded. A restored promotion publishes a fresh request identifier so an old dialog cannot resolve the new prompt.

Both public constructors accept `utcNowProvider`. For a Unity clock that is unaffected by system clock changes, capture a UTC origin once and add elapsed `Stopwatch` time in the provider. The framework does not create a timer or poll clocks automatically.

Draw offers survive the offering player's move and expire when the recipient moves. The side-aware `OfferDrawAsync`, `AcceptDrawAsync`, `DeclineDrawAsync`, and `ResignAsync` overloads support off-turn actions; hosts must authenticate the side. A player cannot accept their own offer. Resignation draws when the opponent cannot possibly mate according to the conservative material test. `ClaimDrawAsync(intendedMove)` declares a legal move that would establish threefold repetition or the fifty-move threshold, leaving the board unchanged.

Loaded positions now use the same basic validation as game review: both kings, legal pawn ranks and piece counts, consistent castling and en-passant fields, and no check against the inactive king. `Fen.Parse` remains syntactic; use `fen.ValidateForPlay()` to validate authored content before starting a match. Validation does not prove historical reachability or solve every blocked dead position.

## Optional engine analysis

`StartAsync` and `CreateAsync(enginePath, ...)` start the primary engine. With automatic analysis disabled, the ponder and classifier processes remain stopped. `StartSearchAsync` starts and configures the ponder worker on demand; an uncached `ClassifyMoveAsync` request starts the classifier worker on demand. `SearchAsync` uses the primary engine. Automatic pondering and scored classification after each position change require `EnableBackgroundAnalysis = true`; this explicitly starts both optional workers and defaults to `false`.

`IsStarted` describes the primary engine. `IsHealthy` includes the primary engine and each running optional worker. Configuration commands target only currently started engines; a worker started later uses its own defaults and the configured coordinator search settings. Stopping does not contact workers that were never started. A failed on-demand worker startup leaves the primary engine available; failed automatic-analysis startup rolls back the engines started by that startup sequence.

`new UciCoordinatorOptions()` uses the documented defaults. Sessions validate resource budgets and enum policies at construction; `default(UciCoordinatorOptions)` is an uninitialized, invalid configuration.

Current-position searches send the base FEN and complete played history so the engine retains repetition context. Terminal results cancel automatic classification and pondering, and discard obsolete search hints.

Queued analysis notifications recheck their generation when the UI context delivers them. Results from a previous position cannot overwrite the Unity view after moving, undoing, loading, ending the match, or disposing the session. Gameplay event history is retained.

Ephemeral `StateChanged` snapshots also check a shared state version, preventing an older worker update from overwriting a newer paused clock or draw offer while the board position stays the same. Typed gameplay events and typed evaluation/classification results keep their own history and generation semantics.

`ClassificationCompleted` reports lifecycle completion. While its classification generation remains current, queued typed and canonical completion notifications survive subsequent evaluations or clock/draw metadata changes and read the latest state when delivered. Loading another position, moving, undoing, ending the game or disposing the session invalidates obsolete completions.

Migration: undo no longer resets both clocks to their initial values. Draw-offer expiry follows the recipient's move. Loaded positions that violate playable-position constraints now throw before replacing the current game. Uninitialized or invalid coordinator options fail at construction.

```csharp
using Bezoro.Chess.UCI.API.Types;

var options = UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true };
await using var session = await UciGameEngineSession.CreateAsync(
    enginePath, options: options, ct: cancellationToken);
await session.LoadMatchAsync(PlayableMatchSetup.Standard, cancellationToken);

// Engine work runs separately. Loading and moving never await its results.
var scoredMoves = await session.WaitForClassificationAsync(cancellationToken);
```

With automatic analysis disabled, the classification wait and stream complete with no scored moves. Use `session.CurrentFen.ClassifyMoveFully(move)` for on-demand local move semantics. Start the configured engines before enabling automatic analysis. Gameplay uses standard FEN and UCI coordinates and does not require the non-standard `d` or `go perft` engine extensions.

## Engine-backed usage
### Search an engine-controlled move
```csharp
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.Protocol.API.Types;

await using var session = await UciGameEngineSession.CreateAsync(enginePath, ct: cancellationToken);

await session.ResetAsync(cancellationToken);

session.StateChanged += state =>
{
    Console.WriteLine($"Moves: {state.TotalLegalMoves}, classified: {state.ClassifiedMovesCount}");
};

var engineResult = await session.SearchAsync(
    new SearchParameters { Depth = 10 },
    cancellationToken);

await session.MakeMoveAsync(engineResult.BestMove, cancellationToken);
```

### Drive UI feedback from classified moves
```csharp
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Types;
using Bezoro.Chess.UCI.Protocol.API.Types;

await using var session = await UciGameEngineSession.CreateAsync(
    enginePath,
    options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true },
    ct: cancellationToken);
await session.LoadMatchAsync(PlayableMatchSetup.Standard, cancellationToken);

await foreach (var move in session.StreamClassifiedMovesAsync(cancellationToken))
{
    string color = move.Analysis switch
    {
        { IsMate: true } => "magenta",
        { IsCapture: true } => "red",
        { IsCastling: true } => "yellow",
        { IsPromotion: true } => "cyan",
        _ => "white"
    };

    Console.WriteLine($"{move.Notation} => {color}");
}
```

### Keep a Unity, Godot, or UI thread synchronized
```csharp
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Common.Enums;
using Bezoro.Chess.UCI.Protocol.API.Types;

SynchronizationContext uiContext = SynchronizationContext.Current!;

await using var session = await UciGameEngineSession.CreateAsync(
    enginePath,
    syncContext: uiContext,
    perspectiveColor: 'w',
    whiteController: MatchSideControllerKind.Manual,
    blackController: MatchSideControllerKind.Engine,
    ct: cancellationToken);

session.Ready += () => Console.WriteLine("Engine ready");
session.PositionChanged += state =>
{
    // Safe to bind UI here because callbacks are marshaled to uiContext.
    Console.WriteLine(state.CurrentFen.Raw);
};
session.MoveMade += move => Console.WriteLine($"{move.Actor}: {move.Notation} [{move.KindFlags}]");
session.PromotionRequired += request => ShowPromotionButtons(request.AllowedPromotionPieces);
session.MoveClassified += move => Console.WriteLine($"Highlight {move.Notation}");
session.EvaluationChanged += pv => Console.WriteLine(pv.Raw);
session.EngineThinkingStarted += _ => ShowThinking(true);
session.EngineThinkingStopped += _ => ShowThinking(false);
session.ResultChanged += state => Console.WriteLine($"Result: {state.Result.Reason}");
session.DrawOffered += state => Console.WriteLine($"Draw offered by {state.DrawOfferedBy}");
session.ClockPaused += state => Console.WriteLine($"Clock paused: {state.Clock?.IsPaused}");

await session.LoadMatchAsync(PlayableMatchSetup.Standard, cancellationToken);
await session.StartSearchAsync(cancellationToken);
```

### Prefer the compact typed event stream for new integrations
```csharp
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Types;
using Bezoro.Chess.UCI.Protocol.API.Types;

await using var session = await UciGameEngineSession.CreateAsync(enginePath, ct: cancellationToken);

session.EventPublished += sessionEvent =>
{
    switch (sessionEvent)
    {
        case { Kind: UciGameEngineSessionEventKind.MoveMade, Move: { } move }:
            Console.WriteLine($"{move.Actor}: {move.Notation}");
            break;

        case { Kind: UciGameEngineSessionEventKind.PositionChanged, State: { } state }:
            Console.WriteLine(state.CurrentFen.Raw);
            break;

        case { Kind: UciGameEngineSessionEventKind.PromotionRequired, PromotionRequired: { } request }:
            ShowPromotionButtons(request.AllowedPromotionPieces);
            break;
    }
};

await session.LoadMatchAsync(PlayableMatchSetup.Standard, cancellationToken);
```

### Drive a promotion request/response flow
```csharp
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Common.Enums;
using Bezoro.Chess.UCI.Protocol.API.Types;

await using var session = await UciGameEngineSession.CreateAsync(enginePath, ct: cancellationToken);
await session.LoadMatchAsync(
    new PlayableMatchSetup(Fen.Parse("1r5k/P7/8/8/8/8/8/K7 w - - 0 1")!.Value),
    cancellationToken);

PromotionRequiredEvent pending = default;
session.PromotionRequired += request => pending = request;

await session.MakeMoveAsync("a7a8", cancellationToken);
await session.ChoosePromotionAsync(pending.PendingPromotionId, PieceType.Queen, cancellationToken);
```

### Configure coordinator-level engine behavior
```csharp
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Types;
using Bezoro.Chess.UCI.Protocol.API.Types;

var options = new UciCoordinatorOptions(
    PonderThreads: 2,
    MultiPv: 3,
    ClassificationDepth: 8,
    EngineMoveTimeMs: 750,
    TimeControl: new PlayableMatchTimeControl(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2)),
    ClaimableDrawPolicy: PlayableMatchClaimableDrawPolicy.ClaimRequired,
    DrawOfferPolicy: PlayableMatchDrawOfferPolicy.ExpireOnMove,
    ControlledMoveFallbackPolicy: PlayableMatchControlledMoveFallbackPolicy.UseLocalFallback);

await using var session = await UciGameEngineSession.CreateAsync(
    enginePath,
    options,
    ct: cancellationToken);

await session.SetOptionAsync("Hash", "256", cancellationToken);
await session.SetDebugAsync(false, cancellationToken);
```

### Restore an authored match clock
```csharp
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Types;
using Bezoro.Chess.UCI.Protocol.API.Types;

var options = UciCoordinatorOptions.Default with
{
    TimeControl = new PlayableMatchTimeControl(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2))
};

await using var session = await UciGameEngineSession.CreateAsync(
    enginePath,
    options,
    ct: cancellationToken);

var setup = new PlayableMatchSetup(
    Fen.Default,
    ["e2e4", "e7e5"],
    new PlayableMatchClockSetup(
        whiteRemaining: TimeSpan.FromMinutes(3),
        blackRemaining: TimeSpan.FromMinutes(2),
        delayRemaining: TimeSpan.FromSeconds(1)));

await session.LoadMatchAsync(setup, cancellationToken);
```

Use `PlayableMatchSetup` for save/load or authored setup assets. `PlayableMatchClockSetup` restores authored clock values while the session derives the active color, per-side completed move counts, and active time-control stage from the loaded position. `PlayableMatchClockSetup.FromExactRestore` remains available when a caller needs to provide every clock field explicitly.

## API Reference
### `UciGameEngineSession`
| Member                                                            | Description                                                         |
|-------------------------------------------------------------------|---------------------------------------------------------------------|
| `CreateAsync(enginePath, options, syncContext, perspectiveColor, whiteController, blackController, ct)` | Constructs and starts a ready-to-use game-engine session. |
| `StartAsync(ct)` / `StopAsync(ct)`                                | Starts the primary engine (and opted-in analysis workers), or stops started engines. |
| `ResetAsync(ct)` / `NewGameAsync(ct)`                             | Loads the standard position or clears the local gameplay context.  |
| `LoadMatchAsync(setup, ct)`                                        | Loads a complete `PlayableMatchSetup`, including base FEN, played moves, and optional clock setup. |
| `UpdatePositionAsync(fen, playedMoves, ct)` / `UpdatePositionAsync(fen, playedMoves, clockRestore, ct)` | Obsolete migration shims for older callers; prefer `LoadMatchAsync`. |
| `SetPositionAsync(fenString, ct)`                                 | Convenience wrapper that parses a FEN string.                       |
| `StartSearchAsync(ct)` / `StartSearchAsync(fen, playedMoves, ct)` | Starts infinite pondering search.                                   |
| `StopSearchAsync(ct)`                                             | Stops the current ponder search.                                    |
| `SearchAsync(parameters, ct)`                                     | Runs a bounded quick search and returns `SearchResult`.             |
| `PlayControlledMoveAsync(ct)`                                     | Plays the current side automatically when that side is engine-controlled. |
| `PlayControlledMoveIfNeededAsync(ct)`                             | Plays only when the current side is engine-controlled and the match is still playable; otherwise returns `null`. |
| `ClassifyMoveAsync(move, ct)`                                     | Returns semantic move analysis for one legal move.                  |
| `StreamClassifiedMovesAsync(ct)`                                  | Streams classifications as they complete.                           |
| `WaitForClassificationAsync(ct)`                                  | Waits for all legal moves to be classified.                         |
| `MakeMoveAsync(move, ct)` / `MakeMoveAsync(move, actor, ct)`      | Applies a legal move and refreshes state, optionally tagging the actor. |
| `ChoosePromotionAsync(id, pieceType, ct)`                         | Resolves a pending promotion request and applies the completed move. |
| `UndoAsync(count, ct)`                                            | Rewinds one or more played moves and restarts the high-level clock from its configured initial state. |
| `ResignAsync(ct)`                                                 | Ends the current game immediately by resignation for the side to move. |
| `OfferDrawAsync(ct)` / `AcceptDrawAsync(ct)` / `DeclineDrawAsync(ct)` / `ClaimDrawAsync(ct)` | Drives draw-offer and claimable-draw flows backed by protocol rules. |
| `PauseClockAsync(ct)` / `ResumeClockAsync(ct)`                    | Pauses or resumes the configured match clock.                       |
| `SetOptionAsync(name, value, ct)`                                 | Applies a UCI option to currently started engines.                 |
| `SetDebugAsync(enabled, ct)`                                      | Broadcasts `debug on/off` to currently started engines.             |
| `RegisterAsync(registration, ct)`                                 | Broadcasts `register` to currently started engines.                |
| `GetCurrentFenAsync(ct)`                                          | Returns the current local FEN without engine I/O.                   |
| `PerspectiveColor` / `WhiteController` / `BlackController`        | Controller-neutral playable-side configuration mirrored from protocol. |
| `GetController(side)`                                             | Returns the configured controller for `w` or `b`.                   |
| `State`                                                           | Current immutable snapshot.                                         |
| `EngineInfo` / `AvailableOptions` / `Capabilities`                | Handshake metadata surfaced from the snapshot client.               |
| `EventPublished`                                                   | Compact typed event stream for new integrations.                    |
| `GameStarted`                                                     | Raised when a fresh gameplay session starts.                        |
| `MoveMade`                                                        | Canonical rich move event for applied moves.                        |
| `CaptureMade` / `CastlingMade` / `EnPassantMade`                  | Convenience move projections for common UI hooks.                   |
| `PromotionRequired` / `PromotionChosen`                           | Promotion request/response events for UI-driven choice flows.       |
| `Check` / `Checkmate` / `Stalemated`                              | Tactical end-state events for applied moves.                        |
| `MoveUndone`                                                      | Raised when moves are undone.                                       |
| `IllegalMoveRejected`                                             | Raised before an illegal move call throws.                          |
| `ResultChanged`                                                   | Raised when the adjudicated match result changes.                   |
| `DrawOffered` / `DrawDeclined`                                    | Raised when a draw offer is published or cleared.                   |
| `ClockPaused` / `ClockResumed`                                    | Raised when the configured match clock is paused or resumed.        |
| `TurnChanged`                                                     | Raised when the active side changes.                                |
| `PositionLoaded` / `PositionChanged`                              | Raised for explicit loads and visible board snapshot updates.       |
| `LegalMovesUpdated`                                               | Raised when the visible legal move set changes.                     |
| `SearchStateChanged`                                              | Raised when the session enters or leaves searching state.           |
| `EngineThinkingStarted` / `EngineThinkingStopped`                 | Raised for UI-facing engine-think lifecycle transitions.            |
| `EvaluationChanged`                                                | Raised for new principal variations from the ponder engine.         |
| `EvaluationUpdated`                                                | Obsolete alias for `EvaluationChanged`; prefer `EvaluationChanged` or `EventPublished`. |
| `BestMoveChanged`                                                 | Raised when the current best/ponder move pair changes.              |
| `MoveClassified`                                                   | Raised for each newly classified legal move.                        |
| `MoveClassificationUpdated`                                       | Obsolete alias for `MoveClassified`; prefer `MoveClassified` or `EventPublished`. |
| `ClassificationCompleted`                                         | Raised when the current position's move classification is complete. |
| `GameOver`                                                        | Raised when the match reaches a terminal result.                    |
| `StateChanged`, `Ready`, `Stopped`, `Error`                       | Coarser lifecycle and integration events.                           |
| `EngineError`                                                     | Obsolete alias for `Error`; prefer `Error` or `EventPublished`.     |

### `GameMoveEvent`
| Property | Meaning |
| --- | --- |
| `GameId` / `MoveId` / `Ply` | Stable gameplay identifiers for event-driven UI logic. |
| `Actor` | Who initiated the move, such as `Human` or `Engine`. |
| `Notation`, `From`, `To` | Raw UCI move text plus origin and destination squares. |
| `KindFlags` | Structural move kinds such as capture, en passant, castling, promotion, and double pawn push. |
| `MovingPiece`, `CapturedPiece`, `PromotionPiece` | Fully typed piece payloads for board animation and VFX hooks. |
| `SecondaryPieceMove` | Auxiliary piece movement such as the rook leg of castling. |
| `PreviousFen`, `ResultingFen` | Exact before/after board states. |
| `IsCheck`, `IsCheckmate`, `IsStalemate` | Tactical end-state flags after the move is applied. |
| `Evaluation` | Optional engine evaluation for the resulting position when available. |

### `UciState`
| Property                                              | Meaning                                                        |
|-------------------------------------------------------|----------------------------------------------------------------|
| `BaseFen`                                             | Starting position before `PlayedMoves` are applied.            |
| `CurrentFen`                                          | Effective current position after applied moves.                |
| `PlayedMoves`                                         | Move history from `BaseFen`.                                   |
| `LegalMoves`                                          | Currently legal moves in raw UCI notation.                     |
| `ClassifiedMoves`                                     | Completed move classifications keyed by notation.              |
| `BestMove` / `PonderMove`                             | Best and ponder moves from the current search, when available. |
| `Evaluation`                                          | Latest principal variation from the ponder engine.             |
| `IsSearching`                                         | Whether a ponder search is active.                             |
| `Result` / `ClaimableResult`                          | Current terminal result or a draw that must still be claimed.  |
| `DrawOfferedBy`                                       | Side that currently has an unanswered draw offer, when any.    |
| `Clock`                                               | Current protocol-backed chess-clock snapshot, when configured. |
| `IsCheck`, `IsCheckmate`, `IsStalemate`, `IsGameOver` | Derived convenience flags.                                     |
| `ClassificationProgress`                              | Fraction of legal moves already classified.                    |

### `Move` and `MoveAnalysis`
| Member                                             | Meaning                                                 |
|----------------------------------------------------|---------------------------------------------------------|
| `Move.Notation`                                    | Raw UCI notation like `e2e4`.                           |
| `Move.From` / `Move.To`                            | Source and target squares.                              |
| `Move.Piece` / `Move.MovingSide`                   | Moving piece metadata.                                  |
| `Move.Analysis.IsCapture`                          | True for captures, including en passant.                |
| `Move.Analysis.IsCastling`                         | True for castling moves.                                |
| `Move.Analysis.IsPromotion`                        | True for promotion moves.                               |
| `Move.Analysis.IsCheck` / `IsMate` / `IsStalemate` | Tactical end-state flags derived during classification. |
| `Move.Analysis.Score`                              | Engine score associated with the move.                  |

### `UciCoordinatorOptions`
| Property              | Meaning                                       |
|-----------------------|-----------------------------------------------|
| `PonderThreads`       | Thread count applied to the ponder engine.    |
| `MultiPv`             | MultiPV value applied to the ponder engine.   |
| `ClassificationDepth` | Search depth used during move classification. |
| `EngineMoveTimeMs`    | Move time budget used by `PlayControlledMoveAsync`. |
| `TimeControl`         | Optional protocol-side chess clock configuration. |
| `ClaimableDrawPolicy` | Whether repetition and fifty-move draws are automatic or require an explicit claim. |
| `DrawOfferPolicy`     | Whether pending draw offers expire on the next move. |
| `ControlledMoveFallbackPolicy` | Whether engine-controlled turns may fall back to a legal local move. |
| `EnableBackgroundAnalysis` | Opts into automatic pondering and scored classification after local position updates. Defaults to false. |
| `Default`             | Default configuration with automatic analysis disabled. |

## Design Notes
- This project hides transport/protocol complexity behind one game-facing facade.
- The session now uses the protocol layer's controller-neutral side model, so the same facade can represent manual/manual, manual/engine, or engine/engine local sessions.
- The session exposes one canonical rich `MoveMade` payload plus convenience events so a game engine can react to board snapshots, search updates, move semantics, undo, promotion, and terminal states without reverse-engineering chess rules from raw UCI strings.
- New integrations should prefer `EventPublished`; focused subscriptions should use canonical named events such as `EvaluationChanged`, `MoveClassified`, and `Error`.
- Obsolete compatibility aliases (`EvaluationUpdated`, `MoveClassificationUpdated`, and `EngineError`) remain for source compatibility only.
- Resign, draw offer/claim, adjudicated result, and chess-clock state are projected from protocol-backed match rules rather than reimplemented by the consumer.
- Clock operations share the protocol session's deterministic clock kernel. Moves commit their local position, history, result, and clock before optional engine work runs.
- `UndoAsync` intentionally reloads the retained position and resets both sides to the configured initial time, base stage, and zero completed clock moves. This differs from `UciPlayableMatchSession.UndoMoves`, which restores a retained clock checkpoint and restarts that retained turn.
- Invalid position and clock setups leave the current game intact. Invalid or canceled promotion choices retain the pending request. Undo clears adjudicated results and rebuilds repetition state from retained history.
- The session builds on protocol-owned local FEN and legal-move generation for playable match flow, while engine-specific `d` and `go perft 1` remain available only as optional low-level escape hatches on `UciEngineClient`.
- Protocol types such as `Fen`, `SearchParameters`, and `SearchResult` come from `Bezoro.Chess.UCI.Protocol`; this project uses them rather than redefining them.
- Search and metadata snapshots exposed by the protocol layer are immutable, so session state can safely retain and rebroadcast them across threads.

Gameplay commands serialize concurrent callers. Event callbacks use the supplied `SynchronizationContext`; async state and classification streams receive updates independently of that context. `StreamStateAsync` yields the current snapshot and keeps at most one pending snapshot. Slow consumers receive the latest state and skip intermediate snapshots, so a paused renderer cannot retain an unlimited backlog of board and analysis data. Use `EventPublished` for individual gameplay events. The classified-move stream retains its finite set of move results. Both streams complete on stop or disposal, and disposing a session observes canceled background workers before disposing their engines. Subscribers should await commands instead of synchronously blocking inside event callbacks.

Engine-backed manual gameplay leaves idle engines alone. When analysis is disabled and no search is active or pending, loads, moves, new games, and undo send no ponder stop commands and create no stop tasks. Sessions prune completed background and ponder-stop tasks during subsequent operations. Unfinished tasks remain tracked so disposal can await them before releasing engine resources. Local gameplay does not wait for those tasks.

`UciState.IsCheck` uses local rules. `Fen.Checkers` remains optional metadata from engines supporting `d` and is not required for gameplay. Terminal outcomes remain fixed until undo or loading another match, and terminal clocks stay paused.

`BoardState.Positions` is an immutable collection of sixty-four square values. `MoveScore.TryParse` shares the protocol info grammar, including repeated spaces, terminal `string`/`pv` fields, and last-score replacement. Scores embedded in engine text are not evaluations.

`ParsedMove.Notation` retains the promotion suffix and normalizes coordinates. Promotion pieces use the color implied by their destination rank. Bishop underpromotions from the b-file, such as `b7b8b`, preserve the complete move in board events and classification results.

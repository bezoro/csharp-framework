# Bezoro.Chess.Study

Optional, engine-free study trees and PGN/SAN interchange for standard chess. Depends on the local rules in `Bezoro.Chess.UCI.Protocol`; it starts no Stockfish process. Unity can display any selected node using the same renderer as a live game.

```csharp
var study = Pgn.Parse(savedPgn);
study.Redo();                     // follow the main line
study.PlaySan("c5");              // select or create a continuation
study.Undo();                     // preserve that continuation
study.PlaySan("e5");              // create a sibling variation
string saved = Pgn.Write(study);  // every variation, comment and annotation
```

`ChessStudy` owns a mutable cursor/tree and belongs to one thread. Its FEN positions are immutable and navigation is constant time: no replay, engine calls, tasks or clocks. Nodes share their prefixes; child lists and annotation lists are allocated only when used. A configurable node budget includes the root. `GoTo` rejects nodes from another study. `GetCurrentMoves()` copies only on request, suitable for a separate review service with the root FEN. Study undo never changes a live match or its result/clocks; casual live takebacks are a separate match policy.

`ChessNotation.ToSan` and `ParseSan` validate legality locally, including legal-piece disambiguation, castling, en passant, promotion, check and mate. Parsing accepts `0-0`/`0-0-0` and omitted check suffixes; a supplied suffix must be correct. English SAN is independent of the display language. These are study/serialization operations, not required work on every game move.

`Pgn.Parse` imports **one** game, requiring a termination marker. It accepts tag escapes, move numbers (including `1.e4`), brace/semicolon comments, NAGs (`$0`–`$255`), symbolic move annotations and nested RAVs. It preserves tags, comments, annotations and variation order, including custom `SetUp`/`FEN` starts and black-to-move counters. Variation introductions are attached before their first move. Repeated move paths remain distinct during import, so a duplicate variation cannot replace the main line; interactive `Play` reuses the first matching continuation. Results must agree between header and movetext. Unsupported variants, collections, malformed or illegal moves and budget overruns fail without returning a partial tree. SetUp/FEN are generated from the actual root on export. Annotation placement and whitespace are normalized; original byte layout is not retained. Comments containing braces use semicolon lines on export. Import uses explicit input/token/node/nesting budgets and an iterative variation stack; export is iterative as well.

The module does not claim identical Chess.com evaluation scores. To analyze a selected line, send `study.Root.Position` plus `study.GetCurrentMoves()` to an explicitly requested local or remote engine review. See [chess review](../../docs/chess-review.md) for the documented classification model and its limits.

`ChessLesson` validates an exercise's accepted UCI solution lines once, then shares its solution tree across independent `StartAttempt()` owners. Learner alternatives are supported; scripted opponent replies must be unambiguous. `TryPlay` rejects wrong moves without changing the board, applies a scripted reply immediately, and reports completion. `Undo` rewinds a learner decision together with its reply; `Reset` also clears mistakes. Hints are queried explicitly. Completion means reaching an authored solution; it does not infer pedagogical quality or engine objectives. Use story-stage IDs from `Bezoro.Progression` to unlock exercises, and keep localized teaching text, assets and reward policy in the game.

```csharp
var lesson = new ChessLesson(Fen.Default, new[] {
    new[] { "e2e4", "e7e5", "g1f3" },
    new[] { "d2d4", "d7d5", "c2c4" }
});
var attempt = lesson.StartAttempt();
var feedback = attempt.TryPlay("e2e4"); // also applies the scripted e7e5
attempt.Undo();                         // returns to the starting position
```

Format reference: [Steven J. Edwards's PGN specification](https://www.saremba.de/chessgml/standards/pgn/pgn-complete.htm).

## Discrete teaching phases

`GuidedLessonDefinition` describes an immutable phase with stable ID, teaching text and setup. `Kind` separates fundamental phases from boss phases; `Fundamentals` identifies earlier material to teach or reinforce. Unlocking and rewards belong to progression. Definitions expose a deterministic `Fingerprint` so a progression catalog can bind completion to the authored content, including its goal and move budget. Runtime clock timestamps are excluded.

The two modes have independent contracts:

- `Scripted` contains explanation steps and exact learner moves, optional scripted replies and locally checked objectives. `RequiredMove` exposes parsed source/target squares only while awaiting learner input. A pending promotion may omit its choice initially, but `AllowedPromotionPieces` keeps the final choice restricted to the authored move.
- `GoalPuzzle` uses `ChessPuzzleDefinition` to require checkmate within a budget of learner moves. The learner may choose any legal move and gets no authored arrow. Starting terminal positions and histories continuing past a mandatory result are rejected. Mandatory draws (stalemate, insufficient material, 75 moves or fivefold repetition) fail the puzzle without requesting an engine defense; checkmate takes precedence. Claimable draws remain optional. Confirmed learner plies spend the budget; setup history and opponent moves do not. The host requests a real engine defense when `NeedsOpponentMove` is returned. The Study player neither chooses engine moves nor starts processes.

`GuidedLessonPlayer` owns the portable state machine. The host loads the setup, acknowledges it, submits learner requests and confirms actual `GuidedLessonPosition` snapshots containing base position, move history and current FEN. Objectives and full position/history deltas are verified with Protocol rules, independently of engine checkers or claimed results. Search/clock/noop and unrelated move updates cannot advance an accepted request. Replies require one confirmed legal ply. Puzzle undo accepts only an earlier confirmed history prefix and recomputes the learner budget.

`Project().GetAllowedMoves(hostLegalMoves)` supplies the same constraint for board selection and target hints. Loading, explanations, reply processing and terminal states block learner input. A successful attempt issues `ChessLessonCompletion`, whose constructor is internal and whose lesson ID, definition fingerprint, attempt ID and mistake count can be consumed by progression. Reset/new attempts clear the current receipt. UI assets, localization, cancellation generations and asynchronous operation ownership stay in their adapters.

Undo is also projected as a rule. Scripted phases require restarting the phase. Goal puzzles can undo a completed learner decision together with its defense, or just the learner's final move, without crossing authored setup history. Pending operations block undo. The host consumes `AcceptsUndoRequests` and `GetUndoPlyCount` and sends `ObserveUndo` only after an explicit undo command succeeds; a delayed ordinary state update must never be interpreted as an undo.

The existing `ChessLesson` solution tree remains useful for synchronous branching exercises. It cannot express explanation-only steps, asynchronous confirmation phases, final scripted replies or a free-choice checkmate budget, so these teaching phases use a separate player while sharing the same Protocol legality, move parsing and classification APIs. `GuidedLessonValidator` aggregates authoring diagnostics, and `GetPositionBeforeStep` provides rule-backed authoring previews without a live match.

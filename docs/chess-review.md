# Chess review architecture and research

Research date: 2026-10-29. The implementation separates local play from optional engine analysis. It does not call Chess.com services or depend on Chess.com accounts.

## Publicly documented behavior

Chess.com describes move quality as a change in expected game points, using engine evaluation and player rating. Its published thresholds distinguish Best, Excellent, Good, Inaccuracy, Mistake, and Blunder. Special labels need additional context. The documentation describes Brilliant as a good sacrifice, Great as a critical move, and Miss as an unconverted opportunity. It does not publish a complete rating model or the detailed selection algorithms. See [move classification](https://support.chess.com/en/articles/8572705-how-are-moves-classified-what-is-a-blunder-or-brilliant-etc).

[CAPS2 documentation](https://support.chess.com/en/articles/8708970-how-is-accuracy-in-analysis-determined) explains the accuracy score's purpose and broad behavior, without giving a reproducible formula. Identical CAPS2 results cannot be promised from that description.

Chess.com also distinguishes a quick local evaluation from deeper server-side Game Review, whose classifications may change. The framework exposes explicit analysis budgets so an application can offer those tiers without delaying local play. See [quick analysis versus Game Review](https://support.chess.com/en/articles/11845102-why-did-my-move-classification-change-in-game-review).

## Implementation

`UciPlayableMatchSession` owns local rules, history, promotion, results, and optional clocks. Its local constructor starts no process and queues no engine search. `Refresh()` is synchronous. The high-level facade also supports an engine-free constructor; background analysis is opt-in.

`UciGameReviewAnalyzer` owns one review flow over a caller-owned dedicated client or an explicit pool of dedicated clients. Start each client first and give it to one analyzer. Do not share it with game opponents, pondering, another analyzer, or ad hoc commands while a review runs. Reviews on one analyzer are serialized and cancellable. Use `Progress<GameReviewProgress>` to post progress to a captured UI context.

For N moves, the reviewer validates the sequence, evaluates N+1 positions, and reuses neighboring results. Automatically terminal positions are resolved locally. Engine position commands retain the supplied starting FEN and complete move prefix. The result contains immutable evaluations, preferred variations, centipawn loss when comparable, expected-points loss, move quality, and optional annotations. Exact scores are required; lower/upper bounds are not presented as exact probabilities.

A finite node or time budget can interrupt a deeper aspiration-window iteration after a completed iteration already produced an exact score. The reviewer selects the deepest retained exact primary variation that starts with the final best move, preferring the most recent line at equal depth. It does not substitute another move's score or increase the caller's search budget. When no matching exact evidence remains, review fails explicitly; increase the search or PV-retention budget. The selected variation's depth records the evidence actually used.

The default model requests Stockfish WDL. Expected points equal `P(win) + 0.5 * P(draw)`. Both sides of a played-move comparison use the mover's perspective. Engine-recommended moves have zero reported loss to prevent independent search noise from penalizing the preferred move. Missing nonterminal WDL is an explicit unsupported-model error, not a fabricated score.

[Stockfish's own documentation](https://official-stockfish.github.io/docs/stockfish-wiki/Stockfish-FAQ.html) distinguishes evaluations from annotations and explains that its WDL model comes from engine self-play. Those probabilities do not reproduce a rating-adjusted human model. Implement `IExpectedPointsModel` to use a separately calibrated model; ratings are passed through the review options.

## Parallel review and resource budgets

Position searches are independent after the reviewer validates the played sequence. Supply a small pool of distinct clients and set `MaxParallelPositions` to opt into concurrent searches. One worker exclusively uses each active client. The scheduler creates at most one worker task per active client and keeps the existing N+1 position reuse. Local move application, model calls, and annotation policies remain sequential.

```csharp
using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Types;

// Both clients are already started and are reserved for this analyzer.
var options = new GameReviewOptions(new SearchParameters { Nodes = 200_000 })
{
    MaxParallelPositions = 2,
    ThreadsPerEngine = 1,
    HashMegabytesPerEngine = 32,
    MaxGameMoves = 4096
};
var reviewer = new UciGameReviewAnalyzer(
    new[] { analysisClient1, analysisClient2 }, options);
var result = await reviewer.AnalyzeAsync(initialFen, playedMoves, progress, cancellationToken);
```

The defaults are one active search, one thread per engine, sixteen MB of requested hash per engine, and at most 4096 played halfmoves. Parallelism is bounded by the requested limit, the pool size, and the number of nonterminal positions. The reviewer rejects an oversized history instead of truncating it. It copies the move list before its first await; queued reviews build their full FEN and evaluation arrays after acquiring the analyzer gate.

`Threads` and `Hash` are set only when advertised by the engine. Requested values outside an advertised range fail before any client is reconfigured. Configure engines without those options separately. Budget CPU use as active clients times threads per engine. Hash controls the transposition table, not total process memory, and the caller controls how many processes are started. Keep all clients on the same engine build, evaluation network, and tablebase configuration so comparisons use the same model. Parallel scheduling changes hash warmup; even a fixed search budget does not promise identical scores to a serial run.

Evaluations and moves in the returned immutable result always follow the played history. Searches can finish out of order. Progress callbacks are serialized, `CompletedPositions` increases monotonically, and `PositionIndex` identifies the zero-based chronological index that just finished. Use `Progress<GameReviewProgress>` when callbacks should post to Unity's or Godot's captured UI context.

Cancellation or a worker failure cancels sibling workers and awaits their search cleanup before another review can use the pool. The caller retains ownership of all clients and disposes them after the analyzer's work has finished.

## Quality thresholds

The implementation uses the published cutoffs with lower-inclusive intervals. This explicitly resolves boundaries that the public table lists in both neighboring rows. The review pipeline rounds nonnegative expected-points loss to twelve decimal places before classification, removing at most 5e-13 of binary floating-point noise. This preserves exact WDL-derived cutoff values such as 0.70 minus 0.68. The standalone `MoveQualityClassifier` compares caller-supplied doubles without rounding.

| Expected-points loss | Quality |
| --- | --- |
| 0 | Best |
| greater than 0 and less than 0.02 | Excellent |
| 0.02 to less than 0.05 | Good |
| 0.05 to less than 0.10 | Inaccuracy |
| 0.10 to less than 0.20 | Mistake |
| 0.20 to 1 | Blunder |

Quality is distinct from structural flags such as capture, check, castling, and promotion. Mate forecasts are also distinct from an actual board checkmate.

The library's accuracy is `100 * (1 - mean expected-points loss)` for each player's played moves. It is absent when that player has no moves. This metric is deterministic given the evaluations and model, and is explicitly not CAPS2. Never label it as a Chess.com accuracy score in a product.

`IMoveReviewAnnotationPolicy` supports an application's Book, Brilliant, Great, and Miss evidence. The default does not invent those labels. There is no bundled opening corpus, sacrifice classifier, proprietary performance-rating estimator, natural-language coach, voice coach, account history, or server infrastructure. Those are separate application/data services. The API exposes the positions and variations needed to build explanation and retry interfaces without embedding UI in the framework.

## Platform boundaries

A process transport works only where the target permits launching an executable. Use an alternative `IUciTransport` for WebGL, mobile native engines, or remote analysis. The local match does not need that transport. Consumers should persist a base FEN plus the complete move history when they need replay and repetition context, rather than saving only the final FEN.

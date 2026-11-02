# Chess networking boundary

`Bezoro.Chess.Networking` adds an optional authoritative boundary to the local chess rules. Use the networking solution that fits the Unity project (for example its RPC layer) to deliver requests and snapshots. This assembly implements no sockets, connection discovery, matchmaking, login, retry queues, or hosting.

For a shared-device game, use `UciPlayableMatchSession` or `UciGameEngineSession` directly. No network module or engine is required. Both 2D and 3D views can consume the same immutable match state.

## Two players

The authoritative owner creates the match and authenticates each connection. Resolve the connection to its assigned `ChessPlayerSeat` on the host; a client-supplied seat is not authentication.

```csharp
using Bezoro.Chess.Networking;
using Bezoro.Chess.UCI.Protocol.API.Types;

using var match = new AuthoritativeChessMatch(
    Guid.NewGuid(),
    PlayableMatchSetup.Standard,
    timeControl: new PlayableMatchTimeControl(
        TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(3)),
    allowTakebacks: false);

var snapshot = match.GetSnapshot();
var command = new ChessMatchCommand(
    snapshot.MatchId, snapshot.Revision, 1,
    ChessMatchCommandKind.Move, "e2e4");

// The server derives White from this caller's authenticated connection.
var receipt = match.Submit(ChessPlayerSeat.White, command);
// Deliver the receipt to the sender and accepted snapshots to both players.
```

Commands contain a match identity, expected authoritative revision, and per-seat sequence, together with the requested action. They never supply authoritative FEN, clocks, or results. Moves must be complete lowercase UCI notation: resolve a promotion selection in the UI, then submit `a7a8q`, rather than submitting a partial promotion.

Start each seat's sequence at one, and increment only after acceptance. Rejections leave the sequence available for a corrected request. The last identical accepted request per seat returns its original receipt without executing twice. Earlier sequences and reused sequences with different contents are rejected. This retains two receipts rather than an unbounded deduplication history. Because a duplicate receipt may describe an earlier revision, a receiving client must retain its newest snapshot rather than overwrite it with an older receipt.

`GetSnapshot()` refreshes clocks and local adjudication. Call it when the host needs to publish time or detect a timeout, using the host's scheduler. Changing clock values does not increment the revision; a newly adjudicated result does. Project clocks should use a monotonic elapsed-time source, for example a fixed UTC origin plus `Stopwatch.Elapsed`, passed through `utcNowProvider`. Never let a peer set authoritative clock values. The framework supplies no prediction or network-latency compensation policy.

Snapshots retain the original base FEN plus the entire legal move history, preserving repetition information during reconnection. They are immutable and can be read on other threads. RPC serializers may require explicit codecs for `Fen`, immutable collections, and readonly values; serialize FEN as its `Raw` string and reconstruct it with `Fen.Parse`. Do not assume a serializer can reconstruct private FEN constructors automatically. Enforce packet size and request-rate limits before decoding and submitting peer messages.

Off-turn resignation uses the authenticated seat. Draw offers identify their actual offering player, survive that player's own move, and expire when the recipient moves. Only their recipient may accept or decline. Threefold repetition and the fifty-move rule require a claim; mandatory results remain automatic. A `ClaimDraw` command may specify a legal intended `Move` when the draw becomes claimable through that move; claiming does not execute it.

## Takebacks

Takebacks are disabled by default. With `allowTakebacks: true`, one human submits `RequestTakeback` and the opponent submits `AcceptTakeback` or `DeclineTakeback`. Specify the number of halfmoves with `TakebackHalfmoves`. A subsequent move expires the offer. Accepted takebacks restore the local rules' board state, rights, repetition context, clocks, and result. Casual games may reopen after a terminal result when a retained halfmove is undone. Restored clocks restart the retained turn at the takeback time; time lost while a proposal is discussed is not refunded separately.

## Remote engines

The host chooses an engine seat. The Unity client needs no installed executable:

```csharp
using var match = new AuthoritativeChessMatch(
    Guid.NewGuid(), PlayableMatchSetup.Standard,
    engineSeat: ChessPlayerSeat.Black,
    allowTakebacks: true);

// A normal authenticated White command first plays e2e4.
// Implement this interface with the project's RPC/HTTP layer or server service.
IChessMoveService engine = myEngineService;
ChessEngineTurnResult reply = await match.PlayEngineTurnAsync(
    engine, new SearchParameters { MoveTimeMs = 500 }, cancellationToken);
```

`ChessEnginePosition` supplies match identity, captured revision, base FEN, and complete move history. The host selects a finite search budget: depth, nodes, move time, or both players' remaining clock values. A mate target alone or only the inactive player's clock does not bound an engine search and is rejected. The engine service must also enforce deployment resource quotas. At most one engine request is outstanding per match. State changes and disposal signal cancellation. Even a service that ignores cancellation cannot commit a move for an obsolete revision. A returned move is checked against local legal moves before it is applied; an illegal engine response raises an error without changing the position. Services and their transports remain caller-owned.

When takebacks are enabled, the host may call `TakebackEngineGame(halfmoves: 2)` for a casual engine game. Authenticate and authorize that request in the host application. It restores the selected previous position and invalidates any outstanding engine reply. For two humans, use the explicit consent flow instead. Peer commands cannot impersonate the computer-controlled seat.

`UciChessMoveService` adapts a dedicated, already started `UciEngineClient` on a server or supported desktop platform. It serializes engine requests and preserves the full position history. The caller owns and disposes that client. A separate `IChessReviewService` supports explicit study/review; `UciChessReviewService` wraps a configured `UciGameReviewAnalyzer`. Review budgets, concurrency, rating models, and accuracy semantics belong to the configured analyzer. Playing a move never starts a review.

For an explicit review, call `snapshot.CreateEnginePosition()` and pass the result to `IChessReviewService.ReviewAsync`. Retain that request's match identity and revision beside the result. A UI displaying a live match should compare them with its current snapshot before applying position-specific annotations; a finished-game study can retain them with its archived game.

Gameplay uses one state lock and synchronous local rules. An engine request releases that lock while awaiting its service. The adapter starts no background workers, polls no conditions, and allocates cancellation machinery only for explicit engine turns. `maximumHalfmoves` bounds the retained game history (4096 by default); reaching this host budget rejects further moves without inventing a chess result. Configure the limit for the product's maximum supported game length.

Authoritative host persistence, reconnecting transport connections, spectators, authenticated accounts, ratings, match archival, matchmaking, deployment, and disconnect/abandonment policies belong to the application. Do not reconstruct a live match from the final FEN alone; persist the complete local match save when clock history and terminal results must survive restart.

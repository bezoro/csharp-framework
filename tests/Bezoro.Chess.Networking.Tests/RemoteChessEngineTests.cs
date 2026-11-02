using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Xunit;

namespace Bezoro.Chess.Networking.Tests;

public sealed class RemoteChessEngineTests
{
    private static readonly SearchParameters Budget = new() { Nodes = 1000 };

    [Fact]
    public async Task EngineReplyUsesCompleteHistoryAndIsCommittedByLocalRules()
    {
        using var match = Create(ChessPlayerSeat.Black);
        var snapshot = match.GetSnapshot();
        match.Submit(ChessPlayerSeat.White, new(snapshot.MatchId, snapshot.Revision, 1, ChessMatchCommandKind.Move, "e2e4"));
        var service = new DelegateService((position, budget, _) =>
        {
            Assert.Equal(snapshot.MatchId, position.MatchId);
            Assert.Equal(1, position.Revision);
            Assert.Equal(Fen.Default, position.BaseFen);
            Assert.Equal(new[] { "e2e4" }, position.Moves);
            Assert.Equal(Budget, budget);
            return Task.FromResult("e7e5");
        });
        var result = await match.PlayEngineTurnAsync(service, Budget);
        Assert.True(result.Applied);
        Assert.Equal(2, result.Snapshot.Revision);
        Assert.Equal(new[] { "e2e4", "e7e5" }, result.Snapshot.State.MoveHistory.Select(x => x.Move));
    }

    [Fact]
    public async Task EngineCannotBeImpersonatedByPeerCommands()
    {
        using var match = Create(ChessPlayerSeat.White);
        var snapshot = match.GetSnapshot();
        Assert.Equal(ChessMatchCommandStatus.NotAuthorized,
            match.Submit(ChessPlayerSeat.White, new(snapshot.MatchId, 0, 1, ChessMatchCommandKind.Move, "e2e4")).Status);
        using var humanTurn = Create(ChessPlayerSeat.Black);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            humanTurn.PlayEngineTurnAsync(new DelegateService((_, _, _) => Task.FromResult("e2e4")), Budget));
    }

    [Fact]
    public async Task StateChangeCancelsOutstandingSearchAndIgnoringCancellationStillCannotCommit()
    {
        using var match = Create(ChessPlayerSeat.White);
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken searchToken = default;
        var pending = match.PlayEngineTurnAsync(new DelegateService((_, _, token) =>
        {
            searchToken = token;
            return answer.Task;
        }), Budget);
        var snapshot = match.GetSnapshot();
        var offer = match.Submit(ChessPlayerSeat.Black, new(snapshot.MatchId, snapshot.Revision, 1, ChessMatchCommandKind.OfferDraw));
        Assert.Equal(ChessMatchCommandStatus.Accepted, offer.Status);
        Assert.True(searchToken.IsCancellationRequested);
        answer.SetResult("e2e4");
        var obsolete = await pending;
        Assert.False(obsolete.Applied);
        Assert.Empty(obsolete.Snapshot.State.MoveHistory);
        Assert.Equal(offer.Snapshot.Revision, obsolete.Snapshot.Revision);
    }

    [Fact]
    public async Task TimeoutDiscoveredByRejectedSecondEngineRequestCancelsFirstRequest()
    {
        var now = DateTimeOffset.UnixEpoch;
        using var match = new AuthoritativeChessMatch(Guid.NewGuid(), PlayableMatchSetup.Standard,
            engineSeat: ChessPlayerSeat.White,
            timeControl: new(TimeSpan.FromSeconds(1), TimeSpan.Zero), utcNowProvider: () => now);
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured = default;
        var service = new DelegateService((_, _, token) => { captured = token; return answer.Task; });
        var first = match.PlayEngineTurnAsync(service, Budget);
        now += TimeSpan.FromSeconds(2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => match.PlayEngineTurnAsync(service, Budget));
        Assert.True(captured.IsCancellationRequested);
        Assert.True(match.GetSnapshot().State.Result.IsTerminal);
        answer.SetResult("e2e4");
        Assert.False((await first).Applied);
    }

    [Fact]
    public async Task OffTurnResignationFreezesClocksCancelsEngineAndRejectsItsLateReply()
    {
        var now = DateTimeOffset.UnixEpoch;
        using var match = new AuthoritativeChessMatch(Guid.NewGuid(), PlayableMatchSetup.Standard,
            engineSeat: ChessPlayerSeat.White,
            timeControl: new(TimeSpan.FromMinutes(5), TimeSpan.Zero), utcNowProvider: () => now);
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken searchToken = default;
        var pending = match.PlayEngineTurnAsync(new DelegateService((_, _, token) =>
        {
            searchToken = token;
            return answer.Task;
        }), Budget);
        now = now.AddSeconds(3);
        var state = match.GetSnapshot();
        var resigned = match.Submit(ChessPlayerSeat.Black,
            new(state.MatchId, state.Revision, 1, ChessMatchCommandKind.Resign));
        Assert.Equal(ChessMatchCommandStatus.Accepted, resigned.Status);
        Assert.Equal(PlayableMatchResultReason.Resignation, resigned.Snapshot.State.Result.Reason);
        Assert.Equal('w', resigned.Snapshot.State.Result.Winner);
        Assert.True(resigned.Snapshot.State.Clock!.Value.IsPaused);
        Assert.True(searchToken.IsCancellationRequested);
        now = now.AddMinutes(10);
        var frozen = match.GetSnapshot();
        Assert.Equal(resigned.Snapshot.State.Clock.Value.WhiteRemaining, frozen.State.Clock!.Value.WhiteRemaining);
        answer.SetResult("e2e4");
        Assert.False((await pending).Applied);
        Assert.Empty(match.GetSnapshot().State.MoveHistory);
        using var restored = AuthoritativeChessMatch.Restore(Guid.NewGuid(), match.CaptureSave(), utcNowProvider: () => now);
        Assert.Equal(resigned.Snapshot.State.Result, restored.GetSnapshot().State.Result);
    }

    [Fact]
    public async Task CallerCancellationLeavesPositionUntouchedAndAllowsNextRequest()
    {
        using var match = Create(ChessPlayerSeat.White);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = match.PlayEngineTurnAsync(new DelegateService(async (_, _, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "e2e4";
        }), Budget, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(match.GetSnapshot().State.MoveHistory);
        Assert.True((await match.PlayEngineTurnAsync(new DelegateService((_, _, _) => Task.FromResult("e2e4")), Budget)).Applied);
    }

    [Fact]
    public async Task ConcurrentRequestsAreBoundedToOneAndIllegalReplyCannotChangeGame()
    {
        using var match = Create(ChessPlayerSeat.White);
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new DelegateService((_, _, _) => answer.Task);
        var pending = match.PlayEngineTurnAsync(service, Budget);
        await Assert.ThrowsAsync<InvalidOperationException>(() => match.PlayEngineTurnAsync(service, Budget));
        answer.SetResult("e2e5");
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        Assert.Empty(match.GetSnapshot().State.MoveHistory);
    }

    [Fact]
    public async Task LegalReplyOutsideRequestedSearchSubsetIsRejectedBeforeCommit()
    {
        using var match = Create(ChessPlayerSeat.White);
        var restricted = new SearchParameters { Nodes = 1000, SearchMoves = ["e2e4"] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => match.PlayEngineTurnAsync(
            new DelegateService((_, _, _) => Task.FromResult("d2d4")), restricted));
        Assert.Empty(match.GetSnapshot().State.MoveHistory);
        Assert.Equal(0, match.GetSnapshot().Revision);
        Assert.True((await match.PlayEngineTurnAsync(
            new DelegateService((_, _, _) => Task.FromResult("e2e4")), restricted)).Applied);
    }

    [Fact]
    public async Task RequestedSearchSubsetMustBeLegalBeforeCallingService()
    {
        using var match = Create(ChessPlayerSeat.White);
        var service = new DelegateService((_, _, _) => throw new Xunit.Sdk.XunitException("Service must not be called."));
        await Assert.ThrowsAsync<ArgumentException>(() => match.PlayEngineTurnAsync(service,
            new() { Nodes = 1000, SearchMoves = ["e2e5"] }));
        Assert.Empty(match.GetSnapshot().State.MoveHistory);
        Assert.True((await match.PlayEngineTurnAsync(
            new DelegateService((_, _, _) => Task.FromResult("e2e4")),
            new() { Nodes = 1000, SearchMoves = ["E2E4"] })).Applied);
    }

    [Fact]
    public async Task DirectUciServiceRejectsNonPlayableBaseBeforeContactingEngine()
    {
        await using var unstarted = new UciEngineClient("unused-engine-path");
        var service = new UciChessMoveService(unstarted);
        var position = new ChessEnginePosition(Guid.NewGuid(), 0,
            Fen.Parse("7k/8/8/8/8/8/8/8 w - - 0 1")!.Value, []);
        await Assert.ThrowsAsync<ArgumentException>(() => service.FindMoveAsync(position, Budget));
        Assert.False(unstarted.IsStarted);
    }

    [Fact]
    public async Task ExhaustedRevisionRejectsEngineTurnBeforeServiceOrBoardMutation()
    {
        using var match = Create(ChessPlayerSeat.White);
        typeof(AuthoritativeChessMatch).GetField("_revision", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(match, long.MaxValue);
        var service = new DelegateService((_, _, _) => throw new Xunit.Sdk.XunitException("Service must not be called."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => match.PlayEngineTurnAsync(service, Budget));
        Assert.Empty(match.GetSnapshot().State.MoveHistory);
    }

    [Fact]
    public async Task UnboundedSearchModesAreRejectedBeforeCallingService()
    {
        using var match = Create(ChessPlayerSeat.White);
        var service = new DelegateService((_, _, _) => throw new Xunit.Sdk.XunitException("Service must not be called."));
        await Assert.ThrowsAsync<ArgumentException>(() => match.PlayEngineTurnAsync(service, default));
        await Assert.ThrowsAsync<ArgumentException>(() => match.PlayEngineTurnAsync(service, new() { Infinite = true }));
        await Assert.ThrowsAsync<ArgumentException>(() => match.PlayEngineTurnAsync(service, new() { Ponder = true, Nodes = 100 }));
        await Assert.ThrowsAsync<ArgumentException>(() => match.PlayEngineTurnAsync(service, new() { Mate = 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => match.PlayEngineTurnAsync(service, new() { WhiteTimeMs = 1000 }));
    }

    [Fact]
    public async Task HostAuthorizedEngineTakebackInvalidatesSearchAndRestoresGame()
    {
        using var match = Create(ChessPlayerSeat.Black, allowTakebacks: true);
        var initial = match.GetSnapshot();
        match.Submit(ChessPlayerSeat.White, new(initial.MatchId, 0, 1, ChessMatchCommandKind.Move, "e2e4"));
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var pending = match.PlayEngineTurnAsync(new DelegateService((_, _, ct) => { token = ct; return answer.Task; }), Budget);
        var restored = match.TakebackEngineGame(1);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(initial.State.Fen, restored.State.Fen);
        answer.SetResult("e7e5");
        Assert.False((await pending).Applied);
        Assert.Empty(match.GetSnapshot().State.MoveHistory);
        using var ranked = Create(ChessPlayerSeat.Black);
        Assert.Throws<InvalidOperationException>(() => ranked.TakebackEngineGame(1));
        using var players = new AuthoritativeChessMatch(Guid.NewGuid(), PlayableMatchSetup.Standard, allowTakebacks: true);
        Assert.Throws<InvalidOperationException>(() => players.TakebackEngineGame(1));
    }

    [Fact]
    public async Task DisposalCancelsInFlightSearch()
    {
        var match = Create(ChessPlayerSeat.White);
        var pending = match.PlayEngineTurnAsync(new DelegateService(async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "e2e4";
        }), Budget);
        match.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static AuthoritativeChessMatch Create(ChessPlayerSeat engineSeat, bool allowTakebacks = false) =>
        new(Guid.NewGuid(), PlayableMatchSetup.Standard, engineSeat: engineSeat, allowTakebacks: allowTakebacks);

    private sealed class DelegateService(Func<ChessEnginePosition, SearchParameters, CancellationToken, Task<string>> handler) : IChessMoveService
    {
        public Task<string> FindMoveAsync(ChessEnginePosition position, SearchParameters search, CancellationToken ct = default) =>
            handler(position, search, ct);
    }
}

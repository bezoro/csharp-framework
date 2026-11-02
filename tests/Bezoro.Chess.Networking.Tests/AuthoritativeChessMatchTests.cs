using Bezoro.Chess.UCI.Protocol.API.Types;
using Xunit;

namespace Bezoro.Chess.Networking.Tests;

public sealed class AuthoritativeChessMatchTests
{
    [Fact]
    public void LocalGameAndReconnectSnapshotPreserveFullHistory()
    {
        using var match = Create();
        var start = match.GetSnapshot();
        Assert.Equal(20, start.State.LegalMoves.Length);
        var white = Submit(match, ChessPlayerSeat.White, 1, ChessMatchCommandKind.Move, "e2e4");
        Assert.Equal(ChessMatchCommandStatus.Accepted, white.Status);
        var black = Submit(match, ChessPlayerSeat.Black, 1, ChessMatchCommandKind.Move, "e7e5");
        Assert.Equal(2, black.Snapshot.Revision);
        Assert.Equal(new[] { "e2e4", "e7e5" }, black.Snapshot.State.MoveHistory.Select(x => x.Move));
        Assert.Equal(black.Snapshot.State.Fen,
            new PlayableMatchSetup(black.Snapshot.BaseFen, black.Snapshot.State.MoveHistory.Select(x => x.Move)).ResolveCurrentFen());
        Assert.Equal(new[] { "e2e4", "e7e5" }, black.Snapshot.CreateEnginePosition().Moves);
        Assert.Equal(black.Snapshot.Revision, black.Snapshot.CreateEnginePosition().Revision);
        Assert.Empty(start.State.MoveHistory);
    }

    [Fact]
    public void RejectsWrongMatchOutOfTurnAndStaleRevisionWithoutChangingPosition()
    {
        using var match = Create();
        var state = match.GetSnapshot();
        var move = new ChessMatchCommand(state.MatchId, 0, 1, ChessMatchCommandKind.Move, "e2e4");
        Assert.Equal(ChessMatchCommandStatus.WrongMatch, match.Submit(ChessPlayerSeat.White, move with { MatchId = Guid.NewGuid() }).Status);
        Assert.Equal(ChessMatchCommandStatus.NotAuthorized, match.Submit(ChessPlayerSeat.Black, move).Status);
        Assert.Equal(ChessMatchCommandStatus.StaleRevision, match.Submit(ChessPlayerSeat.White, move with { ExpectedRevision = 99 }).Status);
        Assert.Equal(state.State.Fen, match.GetSnapshot().State.Fen);
        Assert.Equal(ChessMatchCommandStatus.Accepted, match.Submit(ChessPlayerSeat.White, move).Status);
    }

    [Fact]
    public void LastAcceptedCommandIsIdempotentButConflictingAndOlderSequencesAreRejected()
    {
        using var match = Create();
        var state = match.GetSnapshot();
        var move = new ChessMatchCommand(state.MatchId, 0, 1, ChessMatchCommandKind.Move, "e2e4");
        var receipt = match.Submit(ChessPlayerSeat.White, move);
        Submit(match, ChessPlayerSeat.Black, 1, ChessMatchCommandKind.Move, "e7e5");
        Assert.Equal(receipt, match.Submit(ChessPlayerSeat.White, move));
        Assert.Equal(ChessMatchCommandStatus.InvalidSequence,
            match.Submit(ChessPlayerSeat.White, move with { Move = "d2d4" }).Status);
        Assert.Equal(ChessMatchCommandStatus.InvalidSequence,
            match.Submit(ChessPlayerSeat.White, move with { Sequence = 0 }).Status);
        Assert.Equal(2, match.GetSnapshot().State.MoveHistory.Length);
        Submit(match, ChessPlayerSeat.White, 2, ChessMatchCommandKind.Move, "g1f3");
        Assert.Equal(ChessMatchCommandStatus.InvalidSequence, match.Submit(ChessPlayerSeat.White, move).Status);
    }

    [Theory]
    [InlineData("e2e5")]
    [InlineData("E2E4")]
    [InlineData("e2e4\nquit")]
    [InlineData(null)]
    public void IllegalMovesDoNotConsumeASequence(string? move)
    {
        using var match = Create();
        Assert.Equal(ChessMatchCommandStatus.InvalidAction, Submit(match, ChessPlayerSeat.White, 1, ChessMatchCommandKind.Move, move).Status);
        Assert.Equal(ChessMatchCommandStatus.Accepted, Submit(match, ChessPlayerSeat.White, 1, ChessMatchCommandKind.Move, "e2e4").Status);
    }

    [Fact]
    public void ResignationUsesAuthenticatedSeatOutsideItsTurn()
    {
        using var match = Create();
        var receipt = Submit(match, ChessPlayerSeat.Black, 1, ChessMatchCommandKind.Resign);
        Assert.Equal(ChessMatchCommandStatus.Accepted, receipt.Status);
        Assert.Equal('w', receipt.Snapshot.State.Result.Winner);
        Assert.Equal(PlayableMatchResultReason.Resignation, receipt.Snapshot.State.Result.Reason);
        Assert.Equal(ChessMatchCommandStatus.MatchFinished, Submit(match, ChessPlayerSeat.White, 1, ChessMatchCommandKind.Move, "e2e4").Status);
    }

    [Fact]
    public void OnlyOpponentMayAcceptOrDeclineDrawAndOfferSurvivesOfferersMove()
    {
        using var match = Create();
        Submit(match, ChessPlayerSeat.White, 1, ChessMatchCommandKind.OfferDraw);
        Assert.Equal(ChessMatchCommandStatus.NotAuthorized, Submit(match, ChessPlayerSeat.White, 2, ChessMatchCommandKind.AcceptDraw).Status);
        Submit(match, ChessPlayerSeat.White, 2, ChessMatchCommandKind.Move, "e2e4");
        Assert.Equal('w', match.GetSnapshot().State.DrawOfferedBy);
        var result = Submit(match, ChessPlayerSeat.Black, 1, ChessMatchCommandKind.AcceptDraw);
        Assert.Equal(PlayableMatchResultReason.DrawAgreement, result.Snapshot.State.Result.Reason);
    }

    [Fact]
    public void OpponentsMoveDeclinesDrawButIllegalMovePreservesIt()
    {
        using var match = Create();
        Submit(match, ChessPlayerSeat.Black, 1, ChessMatchCommandKind.OfferDraw);
        Submit(match, ChessPlayerSeat.White, 1, ChessMatchCommandKind.Move, "e2e5");
        Assert.Equal('b', match.GetSnapshot().State.DrawOfferedBy);
        Submit(match, ChessPlayerSeat.White, 1, ChessMatchCommandKind.Move, "e2e4");
        Assert.Null(match.GetSnapshot().State.DrawOfferedBy);
    }

    [Fact]
    public void TakebackRequiresOpponentConsentAndRestoresHistory()
    {
        using var match = Create(allowTakebacks: true);
        var initial = match.GetSnapshot();
        Submit(match, ChessPlayerSeat.White, 1, ChessMatchCommandKind.Move, "e2e4");
        Submit(match, ChessPlayerSeat.Black, 1, ChessMatchCommandKind.Move, "e7e5");
        var proposed = Submit(match, ChessPlayerSeat.White, 2, ChessMatchCommandKind.RequestTakeback, halfmoves: 2);
        Assert.Equal(new ChessTakebackOffer(ChessPlayerSeat.White, 2), proposed.Snapshot.TakebackOffer);
        Assert.Equal(ChessMatchCommandStatus.NotAuthorized, Submit(match, ChessPlayerSeat.White, 3, ChessMatchCommandKind.AcceptTakeback).Status);
        var accepted = Submit(match, ChessPlayerSeat.Black, 2, ChessMatchCommandKind.AcceptTakeback);
        Assert.Equal(initial.State.Fen, accepted.Snapshot.State.Fen);
        Assert.Empty(accepted.Snapshot.State.MoveHistory);
        Assert.Null(accepted.Snapshot.TakebackOffer);
        Assert.Equal(4, accepted.Snapshot.Revision);
    }

    [Fact]
    public void TakebackCanReopenCasualFinishedGameAndFollowingMoveExpiresProposal()
    {
        using var match = Create(allowTakebacks: true);
        Submit(match, ChessPlayerSeat.White, 1, ChessMatchCommandKind.Move, "e2e4");
        Submit(match, ChessPlayerSeat.Black, 1, ChessMatchCommandKind.Resign);
        Submit(match, ChessPlayerSeat.White, 2, ChessMatchCommandKind.RequestTakeback);
        Submit(match, ChessPlayerSeat.Black, 2, ChessMatchCommandKind.AcceptTakeback);
        Assert.False(match.GetSnapshot().State.Result.IsTerminal);
        Submit(match, ChessPlayerSeat.White, 3, ChessMatchCommandKind.Move, "d2d4");
        Submit(match, ChessPlayerSeat.White, 4, ChessMatchCommandKind.RequestTakeback);
        Submit(match, ChessPlayerSeat.Black, 3, ChessMatchCommandKind.Move, "d7d5");
        Assert.Null(match.GetSnapshot().TakebackOffer);
    }

    [Fact]
    public void TakebackIsDisabledByDefaultAndInvalidCountsAreRejected()
    {
        using var disabled = Create();
        Assert.Equal(ChessMatchCommandStatus.NotAuthorized, Submit(disabled, ChessPlayerSeat.White, 1, ChessMatchCommandKind.RequestTakeback).Status);
        using var enabled = Create(allowTakebacks: true);
        Assert.Equal(ChessMatchCommandStatus.InvalidAction, Submit(enabled, ChessPlayerSeat.White, 1, ChessMatchCommandKind.RequestTakeback, halfmoves: -1).Status);
        Assert.Equal(ChessMatchCommandStatus.InvalidAction, Submit(enabled, ChessPlayerSeat.White, 1, ChessMatchCommandKind.RequestTakeback, halfmoves: int.MaxValue).Status);
    }

    [Fact]
    public void HistoryBudgetIsEnforcedBeforeAnotherMove()
    {
        using var match = Create(maximumHalfmoves: 1);
        Submit(match, ChessPlayerSeat.White, 1, ChessMatchCommandKind.Move, "e2e4");
        Assert.Equal(ChessMatchCommandStatus.MoveLimitReached, Submit(match, ChessPlayerSeat.Black, 1, ChessMatchCommandKind.Move, "e7e5").Status);
    }

    [Fact]
    public void HostSaveRestoresGameWithNewIdentityAndFreshCommandStream()
    {
        var now = DateTimeOffset.UnixEpoch;
        using var original = Create(timeControl: new(TimeSpan.FromSeconds(30), TimeSpan.Zero), now: () => now);
        var identity = original.GetSnapshot().MatchId;
        now += TimeSpan.FromSeconds(3);
        Submit(original, ChessPlayerSeat.White, 1, ChessMatchCommandKind.Move, "e2e4");
        now += TimeSpan.FromSeconds(4);
        var saved = original.CaptureSave();
        now += TimeSpan.FromHours(1);
        using var restored = AuthoritativeChessMatch.Restore(Guid.NewGuid(), saved, utcNowProvider: () => now);
        var snapshot = restored.GetSnapshot();
        Assert.NotEqual(identity, snapshot.MatchId);
        Assert.Equal(0, snapshot.Revision);
        Assert.Single(snapshot.State.MoveHistory);
        Assert.Equal(TimeSpan.FromSeconds(26), snapshot.State.Clock!.Value.BlackRemaining);
        Assert.Equal(ChessMatchCommandStatus.WrongMatch,
            restored.Submit(ChessPlayerSeat.Black, new(identity, 0, 1, ChessMatchCommandKind.Move, "e7e5")).Status);
        Assert.Equal(ChessMatchCommandStatus.Accepted,
            Submit(restored, ChessPlayerSeat.Black, 1, ChessMatchCommandKind.Move, "e7e5").Status);
    }

    [Fact]
    public void HostClockAdjudicatesTimeoutExactlyOnceAndRejectsLateMove()
    {
        var now = DateTimeOffset.UnixEpoch;
        using var match = Create(timeControl: new(TimeSpan.FromSeconds(5), TimeSpan.Zero), now: () => now);
        var initial = match.GetSnapshot();
        now = now.AddSeconds(6);
        var timeout = match.GetSnapshot();
        Assert.True(timeout.State.Result.IsTerminal);
        Assert.Equal('b', timeout.State.Result.Winner);
        Assert.Equal(initial.Revision + 1, timeout.Revision);
        Assert.Equal(timeout.Revision, match.GetSnapshot().Revision);
        Assert.Equal(ChessMatchCommandStatus.MatchFinished, Submit(match, ChessPlayerSeat.White, 1, ChessMatchCommandKind.Move, "e2e4").Status);
    }

    [Fact]
    public void ConcurrentSameRevisionCommandsCommitAtMostOneMove()
    {
        using var match = Create();
        var state = match.GetSnapshot();
        var results = new ChessMatchCommandReceipt[2];
        Parallel.Invoke(
            () => results[0] = match.Submit(ChessPlayerSeat.White, new(state.MatchId, 0, 1, ChessMatchCommandKind.Move, "e2e4")),
            () => results[1] = match.Submit(ChessPlayerSeat.White, new(state.MatchId, 0, 1, ChessMatchCommandKind.Move, "d2d4")));
        Assert.Single(results, x => x.Status == ChessMatchCommandStatus.Accepted);
        Assert.Single(match.GetSnapshot().State.MoveHistory);
    }

    [Fact]
    public void PromotionMustBeCompleteBeforeSubmittingToHost()
    {
        using var match = new AuthoritativeChessMatch(Guid.NewGuid(), new(Fen.Parse("7k/P7/8/8/8/8/8/7K w - - 0 1")!.Value));
        Assert.Equal(ChessMatchCommandStatus.InvalidAction, Submit(match, ChessPlayerSeat.White, 1, ChessMatchCommandKind.Move, "a7a8").Status);
        Assert.Equal(ChessMatchCommandStatus.Accepted, Submit(match, ChessPlayerSeat.White, 1, ChessMatchCommandKind.Move, "a7a8n").Status);
    }

    [Fact]
    public void IntendedMoveDrawClaimDoesNotExecuteTheDeclaredMove()
    {
        var fen = Fen.Parse("4k3/8/8/8/8/8/8/R3K3 w Q - 99 1")!.Value;
        using var match = new AuthoritativeChessMatch(Guid.NewGuid(), new(fen));
        Assert.Equal(ChessMatchCommandStatus.InvalidAction,
            Submit(match, ChessPlayerSeat.White, 1, ChessMatchCommandKind.ClaimDraw, "a1a9").Status);
        var claimed = Submit(match, ChessPlayerSeat.White, 1, ChessMatchCommandKind.ClaimDraw, "a1a2");
        Assert.Equal(ChessMatchCommandStatus.Accepted, claimed.Status);
        Assert.Equal(PlayableMatchResultReason.FiftyMoveRule, claimed.Snapshot.State.Result.Reason);
        Assert.Equal(fen, claimed.Snapshot.State.Fen);
        Assert.Empty(claimed.Snapshot.State.MoveHistory);
    }

    [Fact]
    public void InvalidSeatsAndDisposedOwnersFailExplicitly()
    {
        var match = Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => match.Submit((ChessPlayerSeat)99, default));
        match.Dispose();
        match.Dispose();
        Assert.Throws<ObjectDisposedException>(() => match.GetSnapshot());
        Assert.Throws<ObjectDisposedException>(() => match.Submit(ChessPlayerSeat.White, default));
        Assert.Throws<ArgumentException>(() => new AuthoritativeChessMatch(Guid.Empty, PlayableMatchSetup.Standard));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(maximumHalfmoves: 0));
    }

    private static AuthoritativeChessMatch Create(
        bool allowTakebacks = false, int maximumHalfmoves = 4096,
        PlayableMatchTimeControl? timeControl = null, Func<DateTimeOffset>? now = null) =>
        new(Guid.NewGuid(), PlayableMatchSetup.Standard, timeControl,
            allowTakebacks: allowTakebacks, maximumHalfmoves: maximumHalfmoves, utcNowProvider: now);

    private static ChessMatchCommandReceipt Submit(
        AuthoritativeChessMatch match, ChessPlayerSeat seat, long sequence, ChessMatchCommandKind kind,
        string? move = null, int halfmoves = 1)
    {
        var snapshot = match.GetSnapshot();
        return match.Submit(seat, new(snapshot.MatchId, snapshot.Revision, sequence, kind, move, halfmoves));
    }
}

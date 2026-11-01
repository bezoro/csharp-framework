using System.Collections.Generic;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using FluentAssertions;

namespace Bezoro.Chess.UCI.Protocol.Tests.API;

public sealed class UciPlayableLocalMatchTests
{
    [Fact]
    public async Task DisposedMatch_RejectsCommandsAndAllowsIdempotentCleanup()
    {
        var match = new UciPlayableMatchSession();
        var retained = match.Refresh();
        await match.DisposeAsync();
        match.Dispose();
        await match.DisposeAsync();
        match.Invoking(m => m.Refresh()).Should().Throw<ObjectDisposedException>();
        match.Invoking(m => m.ApplyMove("e2e4")).Should().Throw<ObjectDisposedException>();
        await match.Invoking(m => m.StartNewGameAsync()).Should().ThrowAsync<ObjectDisposedException>();
        retained.LegalMoves.Should().HaveCount(20);
    }

    [Fact]
    public async Task CanceledRequest_DoesNotApplyMoveOrPublishEvents()
    {
        await using var match = new UciPlayableMatchSession();
        await match.StartNewGameAsync();
        var before = match.Refresh();
        var events = new List<PlayableMatchEvent>();
        match.EventOccurred += events.Add;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => match.ProcessAsync(
            new(PlayableMatchRequestKind.ApplyMove, Move: "e2e4"), new CancellationToken(true)));
        match.CurrentState.Should().Be(before);
        match.PlayedMoves.Should().BeEmpty();
        events.Should().BeEmpty();
    }

    [Fact]
    public async Task EngineBackedRefresh_RequiresNoEngineOperationAndSchedulesNoAnalysis()
    {
        await using var playing = new UciEngineClient("engine-must-never-start");
        await using var analysis = new UciEngineClient("engine-must-never-start");
        await using var match = new UciPlayableMatchSession(playing, analysis, 'w',
            MatchSideControllerKind.Manual, MatchSideControllerKind.Manual);
        var state = match.Refresh();
        state.LegalMoves.Should().HaveCount(20);
        match.GetCurrentLegalMoveClassifications().Should().BeEmpty();
        var coordinator = (UciPositionAnalysisCoordinator)typeof(UciPlayableMatchSession)
            .GetField("_positionAnalysis", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(match)!;
        coordinator.ActiveWorker.Should().BeNull();
        Action wait = () => coordinator.GetAnalysisAsync(state.PositionKey);
        wait.Should().Throw<InvalidOperationException>();
        match.CancelAnalysis();
    }

    [Fact]
    public async Task LocalSession_PlaysAndUndoesWithoutAnyEngineClient()
    {
        await using var match = new UciPlayableMatchSession();
        match.HasEngineClients.Should().BeFalse();
        await match.StartNewGameAsync();
        match.Refresh().LegalMoves.Should().HaveCount(20);
        match.ApplyMove("e2e4");
        match.Refresh().Fen.ActiveColor.Should().Be('b');
        match.ApplyMove("e7e5");
        var state = match.Refresh();
        state.MoveHistory.Should().HaveCount(2);
        match.UndoMoves();
        match.Refresh().Fen.Should().Be(Fen.Default.ApplyMove("e2e4"));
        match.TryGetPositionAnalysis(state.PositionKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task LocalSession_AdjudicatesCheckmateWithOrderedEvents()
    {
        await using var match = new UciPlayableMatchSession();
        await match.LoadMatchAsync(new(Fen.Parse("7k/5Q2/7K/8/8/8/8/8 w - - 0 1")!.Value));
        var events = new List<PlayableMatchEventKind>();
        match.EventOccurred += e => events.Add(e.Kind);

        match.ApplyMove("f7g7");
        match.Refresh().Result.Reason.Should().Be(PlayableMatchResultReason.Checkmate);
        events.Should().ContainInOrder(PlayableMatchEventKind.MoveApplied, PlayableMatchEventKind.ResultChanged);
    }

    [Fact]
    public async Task LocalSession_ExplicitEngineAnalysisHasClearFailure()
    {
        await using var match = new UciPlayableMatchSession();
        await match.StartNewGameAsync();
        match.Refresh();
        await match.Invoking(s => s.GetLegalMoveAnalysisAsync()).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*analysis engine*");
    }

    [Theory]
    [InlineData("resign")]
    [InlineData("accept")]
    [InlineData("claim")]
    [InlineData("offer")]
    public async Task LocalSession_ExpiredClockCannotBeOverwrittenByAnotherAction(string action)
    {
        var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        await using var match = new UciPlayableMatchSession(timeControl: new(TimeSpan.FromSeconds(5), TimeSpan.Zero), utcNowProvider: () => now);
        await match.StartNewGameAsync();
        match.Refresh();
        match.OfferDraw();
        now += TimeSpan.FromSeconds(6);

        Action act = action switch
        {
            "resign" => match.Resign,
            "accept" => match.AcceptDraw,
            "claim" => match.ClaimDraw,
            _ => match.OfferDraw
        };
        act.Should().Throw<InvalidOperationException>();
        match.CurrentState.Result.Reason.Should().Be(PlayableMatchResultReason.Timeout);
        match.CurrentState.Result.Winner.Should().Be('b');
    }

    [Fact]
    public async Task LocalSession_ClassifiesMovesOnlyWhenRequested()
    {
        await using var match = new UciPlayableMatchSession();
        await match.StartNewGameAsync();
        match.Refresh();
        match.GetCurrentLegalMoveClassifications().Should().BeEmpty();

        var classifications = await match.WaitForCurrentMoveClassificationsAsync();
        classifications.Should().HaveCount(20);
        classifications["e2e4"].MovingPiece.Should().Be('P');
        match.CancelAnalysis();
    }
}

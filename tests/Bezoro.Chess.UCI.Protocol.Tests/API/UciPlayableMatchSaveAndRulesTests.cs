using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.Internal;
using FluentAssertions;

namespace Bezoro.Chess.UCI.Protocol.Tests.API;

public sealed class UciPlayableMatchSaveAndRulesTests
{
    [Theory]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", "e1g1")]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R b KQkq - 0 1", "e8c8")]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", "a1a8")]
    [InlineData("7k/8/8/3pP3/8/8/8/K7 w - d6 0 42", "e5d6")]
    [InlineData("1r5k/P7/8/8/8/8/8/K7 w - - 0 1", "a7b8n")]
    [InlineData("7k/5Q2/7K/8/8/8/8/8 w - - 99 50", "f7g7")]
    public async Task UndoRestoresCompletePositionAfterSpecialMoves(string rawFen, string move)
    {
        using var match = new UciPlayableMatchSession();
        var before = await match.LoadMatchAsync(new(Fen.Parse(rawFen)!.Value));
        match.ApplyMove(move);
        match.Refresh();
        match.UndoMoves();
        var restored = match.Refresh();
        restored.Fen.Should().Be(before.Fen);
        restored.LegalMoves.Should().Equal(before.LegalMoves);
        restored.Result.Should().Be(before.Result);
        restored.MoveHistory.Should().BeEmpty();
        match.BaseFen.Should().Be(before.Fen);
    }

    [Fact]
    public async Task IntendedRepetitionClaimDoesNotExecuteTheDeclaredMove()
    {
        using var match = new UciPlayableMatchSession(claimableDrawPolicy: PlayableMatchClaimableDrawPolicy.ClaimRequired);
        await match.LoadMatchAsync(new(Fen.Default, ["g1f3", "g8f6", "f3g1", "f6g8", "g1f3", "g8f6", "f3g1"]));
        var before = match.CurrentState;
        match.CanClaimDraw("f6g8").Should().BeTrue();
        match.CanClaimDraw("a7a6").Should().BeFalse();
        match.CurrentState.Should().Be(before);
        match.ClaimDraw("f6g8");
        match.CurrentState.Fen.Should().Be(before.Fen);
        match.CurrentState.MoveHistory.Should().Equal(before.MoveHistory);
        match.CurrentState.Result.Reason.Should().Be(PlayableMatchResultReason.ThreefoldRepetition);
        match.UndoMoves();
        match.Refresh().Result.IsTerminal.Should().BeFalse();
        match.CanClaimDraw("b1c3").Should().BeFalse();
    }

    [Fact]
    public async Task IntendedFiftyMoveClaimRejectsResettingAndIllegalMoves()
    {
        using var match = new UciPlayableMatchSession(claimableDrawPolicy: PlayableMatchClaimableDrawPolicy.ClaimRequired);
        await match.LoadMatchAsync(new(Fen.Parse("7k/8/8/8/8/8/P7/KR6 w - - 99 50")!.Value));
        var fen = match.CurrentState.Fen;
        match.CanClaimDraw("b1b2").Should().BeTrue();
        match.CanClaimDraw("a2a3").Should().BeFalse();
        match.CanClaimDraw("b1c3").Should().BeFalse();
        match.Invoking(m => m.ClaimDraw("a2a3")).Should().Throw<InvalidOperationException>();
        match.CurrentState.Fen.Should().Be(fen);
        await match.ProcessAsync(new(PlayableMatchRequestKind.ClaimDraw, Move: "b1b2"));
        match.CurrentState.Result.Reason.Should().Be(PlayableMatchResultReason.FiftyMoveRule);
        match.PlayedMoves.Should().BeEmpty();
    }

    [Fact]
    public void OffTurnActionsIdentifyTheActorAndDrawRecipient()
    {
        using var match = new UciPlayableMatchSession();
        match.Refresh();
        match.OfferDraw('b');
        match.CurrentState.DrawOfferedBy.Should().Be('b');
        match.Invoking(m => m.AcceptDraw('b')).Should().Throw<InvalidOperationException>();
        match.Invoking(m => m.DeclineDraw('b')).Should().Throw<InvalidOperationException>();
        match.Invoking(m => m.OfferDraw('w')).Should().Throw<InvalidOperationException>();
        match.DeclineDraw('w');
        match.Resign('b');
        match.CurrentState.Result.Winner.Should().Be('w');
    }

    [Fact]
    public async Task ResignationDrawsWhenOpponentCannotPossiblyMate()
    {
        using var match = new UciPlayableMatchSession();
        await match.LoadMatchAsync(new(Fen.Parse("7k/8/8/8/8/8/8/KR6 w - - 0 1")!.Value));
        match.Resign('w');
        match.CurrentState.Result.Reason.Should().Be(PlayableMatchResultReason.Resignation);
        match.CurrentState.Result.IsDraw.Should().BeTrue();
    }

    [Fact]
    public async Task SaveRestoresRunningClockAndUndoHistoryWithoutChargingOfflineTime()
    {
        var now = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var control = new PlayableMatchTimeControl(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        using var source = new UciPlayableMatchSession(timeControl: control, utcNowProvider: () => now);
        await source.StartNewGameAsync();
        source.Refresh();
        now += TimeSpan.FromSeconds(5);
        source.ApplyMove("e2e4");
        source.Refresh();
        now += TimeSpan.FromSeconds(4);
        source.ApplyMove("e7e5");
        now += TimeSpan.FromSeconds(3);
        var save = source.CaptureSave();
        save.ClockHistory.Should().HaveCount(3);
        now += TimeSpan.FromHours(1);
        using var restored = new UciPlayableMatchSession(timeControl: control, utcNowProvider: () => now);
        var state = await restored.LoadSaveAsync(save);
        state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(27));
        state.Clock.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(29));
        restored.CanUndoMoves(2).Should().BeTrue();
        restored.UndoMoves();
        state = restored.Refresh();
        state.Fen.Should().Be(Fen.Default.ApplyMove("e2e4"));
        state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(28));
        state.Clock.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(30));
        state.Clock.Value.DelayRemaining.Should().Be(TimeSpan.FromSeconds(2));
        restored.UndoMoves();
        state = restored.Refresh();
        state.Fen.Should().Be(Fen.Default);
        state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(30));
        state.Clock.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task UndoAfterRestoredMoveUsesOriginalTurnStartClock()
    {
        var now = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var control = new PlayableMatchTimeControl(TimeSpan.FromSeconds(30), TimeSpan.Zero);
        using var original = new UciPlayableMatchSession(timeControl: control, utcNowProvider: () => now);
        await original.StartNewGameAsync();
        original.Refresh();
        now += TimeSpan.FromSeconds(5);
        original.ApplyMove("e2e4");
        now += TimeSpan.FromSeconds(2);
        var save = original.CaptureSave();
        save.ClockHistory[^1].BlackRemaining.Should().Be(TimeSpan.FromSeconds(30));
        save.Setup.Clock!.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(28));
        using var restored = new UciPlayableMatchSession(timeControl: control, utcNowProvider: () => now);
        await restored.LoadSaveAsync(save);
        now += TimeSpan.FromSeconds(1);
        original.ApplyMove("e7e5");
        restored.ApplyMove("e7e5");
        original.UndoMoves();
        restored.UndoMoves();
        restored.Refresh().Clock!.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(30));
        restored.CurrentState.Clock.Should().Be(original.Refresh().Clock);
    }

    [Theory]
    [InlineData(PlayableMatchResultReason.Checkmate, 'w')]
    [InlineData(PlayableMatchResultReason.Checkmate, 'b')]
    [InlineData(PlayableMatchResultReason.Checkmate, null)]
    [InlineData(PlayableMatchResultReason.Stalemate, null)]
    [InlineData(PlayableMatchResultReason.InsufficientMaterial, null)]
    [InlineData(PlayableMatchResultReason.FiftyMoveRule, null)]
    [InlineData(PlayableMatchResultReason.ThreefoldRepetition, null)]
    [InlineData(PlayableMatchResultReason.FivefoldRepetition, null)]
    [InlineData(PlayableMatchResultReason.SeventyFiveMoveRule, null)]
    [InlineData(PlayableMatchResultReason.Timeout, 'b')]
    [InlineData(PlayableMatchResultReason.Resignation, null)]
    public async Task SaveRejectsResultsUnsupportedByTheBoardAndHistory(PlayableMatchResultReason reason, char? winner)
    {
        using var match = new UciPlayableMatchSession();
        var valid = match.CaptureSave();
        var invalid = valid with { Result = new(reason, winner) };
        var before = match.CurrentState;
        await match.Invoking(m => m.LoadSaveAsync(invalid)).Should().ThrowAsync<ArgumentException>();
        match.CurrentState.Should().Be(before);
    }

    [Fact]
    public async Task SaveRejectsWrongCheckmateWinnerAndMissingTerminalResult()
    {
        using var match = new UciPlayableMatchSession();
        await match.LoadMatchAsync(new(Fen.Parse("7k/6Q1/7K/8/8/8/8/8 b - - 1 1")!.Value));
        var valid = match.CaptureSave();
        valid.Result.Winner.Should().Be('w');
        foreach (char? winner in new char?[] { 'b', null })
            await match.Invoking(m => m.LoadSaveAsync(valid with { Result = new(PlayableMatchResultReason.Checkmate, winner) }))
                .Should().ThrowAsync<ArgumentException>();
        await match.Invoking(m => m.LoadSaveAsync(valid with { Result = default })).Should().ThrowAsync<ArgumentException>();
        match.CurrentState.Result.Should().Be(valid.Result);
    }

    [Fact]
    public async Task SaveChecksTimeoutAgainstCurrentClockAndTimeControl()
    {
        var now = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var control = new PlayableMatchTimeControl(TimeSpan.FromSeconds(30), TimeSpan.Zero);
        using var match = new UciPlayableMatchSession(timeControl: control, utcNowProvider: () => now);
        await match.StartNewGameAsync();
        now += TimeSpan.FromSeconds(31);
        var valid = match.CaptureSave();
        (await match.LoadSaveAsync(valid)).Result.Should().Be(new PlayableMatchResult(PlayableMatchResultReason.Timeout, 'b'));
        await match.Invoking(m => m.LoadSaveAsync(valid with { Result = new(PlayableMatchResultReason.Timeout, 'w') }))
            .Should().ThrowAsync<ArgumentException>();
        await match.Invoking(m => m.LoadSaveAsync(valid with { Result = default })).Should().ThrowAsync<ArgumentException>();
        var ignored = new PlayableMatchTimeControl(TimeSpan.FromSeconds(30), TimeSpan.Zero, timeoutPolicy: PlayableMatchTimeoutPolicy.Ignore);
        using var ignoredSession = new UciPlayableMatchSession(timeControl: ignored, utcNowProvider: () => now);
        ignoredSession.Refresh();
        await ignoredSession.Invoking(m => m.LoadSaveAsync(valid with { TimeControl = ignored })).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SaveRequiresTheDeclaredMoveForAnIntendedFiftyMoveClaim()
    {
        using var match = new UciPlayableMatchSession();
        await match.LoadMatchAsync(new(Fen.Parse("7k/8/8/8/8/8/P7/KR6 w - - 99 50")!.Value));
        match.ClaimDraw("b1b2");
        var save = match.CaptureSave();
        save.DeclaredDrawMove.Should().Be("b1b2");
        using var restored = new UciPlayableMatchSession();
        (await restored.LoadSaveAsync(save)).Result.Should().Be(save.Result);
        await restored.Invoking(m => m.LoadSaveAsync(save with { DeclaredDrawMove = null })).Should().ThrowAsync<ArgumentException>();
        await restored.Invoking(m => m.LoadSaveAsync(save with { DeclaredDrawMove = "a2a3" })).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SavePreservesAnIntendedRepetitionClaim()
    {
        using var match = new UciPlayableMatchSession(claimableDrawPolicy: PlayableMatchClaimableDrawPolicy.ClaimRequired);
        await match.LoadMatchAsync(new(Fen.Default, ["g1f3", "g8f6", "f3g1", "f6g8", "g1f3", "g8f6", "f3g1"]));
        match.ClaimDraw("f6g8");
        var save = match.CaptureSave();
        using var restored = new UciPlayableMatchSession(claimableDrawPolicy: PlayableMatchClaimableDrawPolicy.ClaimRequired);
        (await restored.LoadSaveAsync(save)).Result.Should().Be(save.Result);
        await restored.Invoking(m => m.LoadSaveAsync(save with { DeclaredDrawMove = null })).Should().ThrowAsync<ArgumentException>();
        await restored.Invoking(m => m.LoadSaveAsync(save with { DeclaredDrawMove = "a7a6" })).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void UnchangedLocalRefreshReusesImmutableHistoryAndAllocatesNoMemory()
    {
        using var match = new UciPlayableMatchSession();
        match.Refresh();
        match.ApplyMove("e2e4");
        var retained = match.Refresh().MoveHistory;
        for (int index = 0; index < 100; index++) match.Refresh();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++) match.Refresh();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        allocated.Should().Be(0);
        match.CurrentState.MoveHistory.Equals(retained).Should().BeTrue();
        match.ApplyMove("e7e5");
        var advanced = match.Refresh().MoveHistory;
        advanced.Should().HaveCount(2);
        retained.Should().ContainSingle();
        match.UndoMoves();
        match.Refresh().MoveHistory.Should().ContainSingle();
        advanced.Should().HaveCount(2);
    }

    [Fact]
    public async Task ChangedClassificationsInvalidateHistoryWithoutChangingRetainedSnapshots()
    {
        using var match = new UciPlayableMatchSession();
        var retained = (await match.LoadMatchAsync(new(Fen.Default, ["e2e4"]))).MoveHistory;
        retained[0].Classification.IsResolved.Should().BeFalse();
        var coordinator = (MoveClassificationCoordinator)typeof(UciPlayableMatchSession)
            .GetField("_classifications", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(match)!;
        coordinator.Enqueue(Fen.Default.Raw, Fen.Default, Fen.Default.GetLegalMoves());
        await coordinator.WaitAsync(Fen.Default.Raw);
        var enriched = match.Refresh().MoveHistory;
        enriched[0].Classification.IsResolved.Should().BeTrue();
        retained[0].Classification.IsResolved.Should().BeFalse();
        match.Refresh().MoveHistory.Equals(enriched).Should().BeTrue();
    }

    [Fact]
    public async Task RestoreCanExplicitlyChargeTimeSinceSaving()
    {
        var now = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var control = new PlayableMatchTimeControl(TimeSpan.FromSeconds(30), TimeSpan.Zero);
        using var source = new UciPlayableMatchSession(timeControl: control, utcNowProvider: () => now);
        await source.StartNewGameAsync();
        var save = source.CaptureSave();
        now += TimeSpan.FromSeconds(10);
        using var restored = new UciPlayableMatchSession(timeControl: control, utcNowProvider: () => now);
        var state = await restored.LoadSaveAsync(save, countTimeSinceSave: true);
        state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(20));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavePreservesPartiallyConsumedDelayAndPause(bool pause)
    {
        var now = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var control = new PlayableMatchTimeControl(TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.FromSeconds(10));
        using var source = new UciPlayableMatchSession(timeControl: control, utcNowProvider: () => now);
        await source.StartNewGameAsync();
        now += TimeSpan.FromSeconds(3);
        if (pause) source.PauseClock();
        var save = source.CaptureSave();
        now += TimeSpan.FromHours(1);
        using var restored = new UciPlayableMatchSession(timeControl: control, utcNowProvider: () => now);
        var state = await restored.LoadSaveAsync(save);
        state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(30));
        state.Clock.Value.DelayRemaining.Should().Be(TimeSpan.FromSeconds(7));
        state.Clock.Value.IsPaused.Should().Be(pause);
        now += TimeSpan.FromSeconds(5);
        restored.Refresh().Clock!.Value.DelayRemaining.Should().Be(TimeSpan.FromSeconds(pause ? 7 : 2));
    }

    [Fact]
    public async Task RepetitionHistorySurvivesSaveAndAlternativeAfterUndo()
    {
        using var source = new UciPlayableMatchSession(claimableDrawPolicy: PlayableMatchClaimableDrawPolicy.ClaimRequired);
        await source.LoadMatchAsync(new(Fen.Default, ["g1f3", "g8f6", "f3g1", "f6g8", "g1f3", "g8f6", "f3g1", "f6g8"]));
        var save = source.CaptureSave();
        using var restored = new UciPlayableMatchSession(claimableDrawPolicy: PlayableMatchClaimableDrawPolicy.ClaimRequired);
        var state = await restored.LoadSaveAsync(save);
        state.ClaimableResult!.Value.Reason.Should().Be(PlayableMatchResultReason.ThreefoldRepetition);
        restored.UndoMoves(2);
        restored.Refresh();
        restored.ApplyMove("b1c3");
        restored.Refresh().ClaimableResult.Should().BeNull();
        using var incompatiblePolicy = new UciPlayableMatchSession();
        incompatiblePolicy.Refresh();
        await incompatiblePolicy.Invoking(m => m.LoadSaveAsync(save)).Should().ThrowAsync<ArgumentException>();
        incompatiblePolicy.PlayedMoves.Should().BeEmpty();
    }

    [Fact]
    public async Task SavedHistoricalOriginRemainsAnUndoBoundary()
    {
        var control = new PlayableMatchTimeControl(TimeSpan.FromMinutes(1), TimeSpan.Zero);
        using var source = new UciPlayableMatchSession(timeControl: control);
        await source.LoadMatchAsync(new(Fen.Default, ["e2e4", "e7e5"]));
        source.ApplyMove("g1f3");
        var save = source.CaptureSave();
        save.ClockHistoryOriginMoveCount.Should().Be(2);
        using var restored = new UciPlayableMatchSession(timeControl: control);
        await restored.LoadSaveAsync(save);
        restored.CanUndoMoves().Should().BeTrue();
        restored.CanUndoMoves(2).Should().BeFalse();
        restored.UndoMoves();
        restored.Refresh().Fen.Should().Be(Fen.Default.ApplyMove("e2e4").ApplyMove("e7e5"));
    }

    [Fact]
    public async Task InvalidSaveAndCancellationNeverReplaceTheExistingMatch()
    {
        var control = new PlayableMatchTimeControl(TimeSpan.FromMinutes(1), TimeSpan.Zero);
        using var match = new UciPlayableMatchSession(timeControl: control);
        await match.StartNewGameAsync();
        match.Refresh();
        match.ApplyMove("e2e4");
        var before = match.Refresh();
        var save = match.CaptureSave();
        var invalid = save with { ClockHistory = save.ClockHistory.SetItem(1, save.ClockHistory[1] with { BlackMovesCompleted = 9 }) };
        await match.Invoking(m => m.LoadSaveAsync(invalid)).Should().ThrowAsync<ArgumentException>();
        match.CurrentState.Fen.Should().Be(before.Fen);
        await match.Invoking(m => m.LoadSaveAsync(save, ct: new CancellationToken(true))).Should().ThrowAsync<OperationCanceledException>();
        match.PlayedMoves.Should().Equal("e2e4");
        using var wrongControl = new UciPlayableMatchSession(timeControl: new(TimeSpan.FromMinutes(2), TimeSpan.Zero));
        wrongControl.Refresh();
        await wrongControl.Invoking(m => m.LoadSaveAsync(save)).Should().ThrowAsync<ArgumentException>();
        wrongControl.CurrentState.Fen.Should().Be(Fen.Default);
    }

    [Fact]
    public async Task SaveRestoresPendingPromotionAndTerminalResult()
    {
        using var source = new UciPlayableMatchSession();
        await source.LoadMatchAsync(new(Fen.Parse("1r5k/P7/8/8/8/8/8/K7 w - - 0 1")!.Value));
        source.ApplyMove("a7b8");
        var save = source.CaptureSave();
        using var restored = new UciPlayableMatchSession();
        await restored.LoadSaveAsync(save);
        restored.PendingPromotion!.Value.MovePrefix.Should().Be("a7b8");
        restored.ChoosePromotion('r');
        restored.Refresh();
        restored.Resign('b');
        var terminal = restored.CaptureSave();
        await source.LoadSaveAsync(terminal);
        source.CurrentState.Result.Should().Be(terminal.Result);
        source.UndoMoves();
        source.Refresh().Fen.Should().Be(save.Setup.BaseFen);
    }

    [Fact]
    public async Task TimeoutDismissesPromotionBeforeSaving()
    {
        var now = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var control = new PlayableMatchTimeControl(TimeSpan.FromSeconds(1), TimeSpan.Zero);
        using var source = new UciPlayableMatchSession(timeControl: control, utcNowProvider: () => now);
        await source.LoadMatchAsync(new(Fen.Parse("1r5k/P7/8/8/8/8/8/K7 w - - 0 1")!.Value));
        source.ApplyMove("a7b8");
        now += TimeSpan.FromSeconds(2);
        var save = source.CaptureSave();
        save.PendingPromotionMove.Should().BeNull();
        save.Result.Reason.Should().Be(PlayableMatchResultReason.Timeout);
        using var restored = new UciPlayableMatchSession(timeControl: control, utcNowProvider: () => now);
        (await restored.LoadSaveAsync(save)).PendingPromotion.Should().BeNull();
    }

    [Fact]
    public async Task CancelPendingPromotionLeavesCommittedMovesAndClockRunning()
    {
        var now = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var control = new PlayableMatchTimeControl(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        using var match = new UciPlayableMatchSession(timeControl: control, utcNowProvider: () => now);
        var before = await match.LoadMatchAsync(new(Fen.Parse("1r5k/P7/8/8/8/8/8/K7 w - - 0 1")!.Value));
        match.ApplyMove("a7b8");
        now += TimeSpan.FromSeconds(3);
        var events = new List<PlayableMatchEventKind>();
        match.EventOccurred += e => events.Add(e.Kind);
        match.CancelPendingPromotion().Should().BeTrue();
        match.CancelPendingPromotion().Should().BeFalse();
        var state = match.Refresh();
        state.Fen.Should().Be(before.Fen);
        state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(27));
        state.Clock.Value.IsPaused.Should().BeFalse();
        match.PlayedMoves.Should().BeEmpty();
        events.Should().ContainSingle(e => e == PlayableMatchEventKind.PromotionCanceled);
        match.ApplyMove("a7a8q");
        match.Refresh().MoveHistory.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(PlayableMatchClaimableDrawPolicy.Automatic, 2)]
    [InlineData(PlayableMatchClaimableDrawPolicy.ClaimRequired, 4)]
    public async Task LoadedHistoryCannotContinueBeyondRepetitionResult(PlayableMatchClaimableDrawPolicy policy, int loops)
    {
        using var match = new UciPlayableMatchSession(claimableDrawPolicy: policy);
        var moves = Enumerable.Range(0, loops).SelectMany(_ => new[] { "g1f3", "g8f6", "f3g1", "f6g8" }).ToList();
        await match.LoadMatchAsync(new(Fen.Default, moves));
        var before = match.CurrentState;
        moves.Add("e2e4");
        await match.Invoking(m => m.LoadMatchAsync(new(Fen.Default, moves))).Should().ThrowAsync<ArgumentException>();
        match.CurrentState.Should().Be(before);
    }

    [Theory]
    [InlineData("7k/8/8/8/8/8/8/KR6 w - - 150 80", "b1b2")]
    [InlineData("7k/8/8/8/8/8/8/KN6 w - - 0 1", "b1c3")]
    [InlineData("7k/8/8/8/8/8/8/8 w - - 0 1", "h8h7")]
    public async Task InvalidOrTerminalBaseCannotHaveContinuation(string fen, string move)
    {
        using var match = new UciPlayableMatchSession(claimableDrawPolicy: PlayableMatchClaimableDrawPolicy.ClaimRequired);
        match.Refresh();
        await match.Invoking(m => m.LoadMatchAsync(new(Fen.Parse(fen)!.Value, [move]))).Should().ThrowAsync<ArgumentException>();
        match.CurrentState.Fen.Should().Be(Fen.Default);
    }

    [Theory]
    [InlineData("7k/8/8/8/8/8/8/K6R b - - 0 1", true)]
    [InlineData("7k/8/8/8/8/8/8/K6R w - - 0 1", false)]
    public void CheckDetectionUsesSideToMove(string rawFen, bool expected)
    {
        var fen = Fen.Parse(rawFen)!.Value;
        fen.IsInCheck().Should().Be(expected);
    }
}

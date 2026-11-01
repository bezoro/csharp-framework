using System.Collections.Generic;
using System.Threading.Channels;
using Bezoro.Chess.UCI.Protocol.Tests.Domain;
using FluentAssertions;
using NSubstitute;

namespace Bezoro.Chess.UCI.Protocol.Tests.API;

public sealed class UciPlayableMatchSessionRobustnessTests
{
    [Fact]
    public async Task ApplyMove_AfterResignation_RejectsAndPreservesResult()
    {
        await using var match = await Match.CreateAsync();
        match.Session.Resign();

        match.Session.Invoking(s => s.ApplyMove("e2e4")).Should().Throw<InvalidOperationException>();
        match.Session.Invoking(s => s.Resign()).Should().Throw<InvalidOperationException>();
        match.Session.PlayedMoves.Should().BeEmpty();
        (await match.Session.RefreshAsync()).Result.Reason.Should().Be(PlayableMatchResultReason.Resignation);
    }

    [Fact]
    public async Task TerminalResult_FreezesClockAndCannotBeResumed()
    {
        await using var match = await Match.CreateAsync(clocked: true);
        match.Now += TimeSpan.FromSeconds(2);
        match.Session.Resign();
        match.Now += TimeSpan.FromSeconds(20);
        match.Session.ResumeClock();

        var state = await match.Session.RefreshAsync();
        state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(3));
        state.Clock.Value.IsPaused.Should().BeTrue();
        state.Result.Reason.Should().Be(PlayableMatchResultReason.Resignation);
    }

    [Fact]
    public async Task ThreefoldRepetition_EmitsResultOnCompletingMoveBeforeRefresh()
    {
        await using var match = await Match.CreateAsync();
        var results = new List<PlayableMatchResult>();
        match.Session.EventOccurred += e => { if (e.Result.HasValue) results.Add(e.Result.Value); };
        string[] cycle = ["g1f3", "g8f6", "f3g1", "f6g8"];
        for (int i = 0; i < 7; i++)
        {
            match.Session.ApplyMove(cycle[i % 4]);
            await match.Session.RefreshAsync();
        }
        match.Session.ApplyMove("f6g8");

        results.Should().ContainSingle(r => r.Reason == PlayableMatchResultReason.ThreefoldRepetition);
        (await match.Session.RefreshAsync()).Result.Reason.Should().Be(PlayableMatchResultReason.ThreefoldRepetition);
        results.Should().HaveCount(1);
    }

    [Fact]
    public async Task FivefoldRepetition_IsAutomaticUnderClaimRequiredPolicy()
    {
        await using var match = await Match.CreateAsync(claimRequired: true);
        string[] cycle = ["g1f3", "g8f6", "f3g1", "f6g8"];
        for (int i = 0; i < 16; i++)
        {
            match.Session.ApplyMove(cycle[i % 4]);
            await match.Session.RefreshAsync();
        }

        match.Session.CurrentState.Result.Reason.Should().Be(PlayableMatchResultReason.FivefoldRepetition);
        match.Session.CurrentState.ClaimableResult.Should().BeNull();
    }

    [Theory]
    [InlineData("7k/8/8/8/8/8/8/KR6 w - - 150 80", PlayableMatchResultReason.SeventyFiveMoveRule)]
    [InlineData("7k/6Q1/5K2/8/8/8/8/8 b - - 150 80", PlayableMatchResultReason.Checkmate)]
    public async Task SeventyFiveMoveRule_IsAutomaticButCheckmateTakesPriority(string fen, PlayableMatchResultReason reason)
    {
        await using var match = await Match.CreateAsync(claimRequired: true);
        var state = await match.Session.LoadMatchAsync(new(Fen.Parse(fen)!.Value));
        state.Result.Reason.Should().Be(reason);
        state.ClaimableResult.Should().BeNull();
    }

    [Fact]
    public async Task LoadMatch_InvalidRestoredDelay_PreservesExistingPositionAndClock()
    {
        await using var match = await Match.CreateAsync(clocked: true);
        match.Session.ApplyMove("e2e4");
        var before = await match.Session.RefreshAsync();
        var invalidSetup = new PlayableMatchSetup(Fen.Default, clock: new(
            TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1)));

        await match.Session.Invoking(s => s.LoadMatchAsync(invalidSetup)).Should().ThrowAsync<ArgumentOutOfRangeException>();
        match.Session.CurrentState.Should().Be(before);
        match.Session.PlayedMoves.Should().Equal("e2e4");
        (await match.Session.RefreshAsync()).Fen.Should().Be(before.Fen);
    }

    [Fact]
    public async Task ChoosePromotion_AfterFlagFall_PreservesPendingChoiceAndDoesNotApplyMove()
    {
        await using var match = await Match.CreateAsync(clocked: true);
        await match.Session.LoadMatchAsync(new(Fen.Parse("1r5k/P7/8/8/8/8/8/K7 w - - 0 1")!.Value));
        match.Session.ApplyMove("a7a8");
        match.Session.CurrentState.PendingPromotion.Should().NotBeNull();
        match.Now += TimeSpan.FromSeconds(6);

        match.Session.Invoking(s => s.ChoosePromotion('q')).Should().Throw<InvalidOperationException>();
        match.Session.PendingPromotion.Should().NotBeNull();
        match.Session.PlayedMoves.Should().BeEmpty();
        (await match.Session.RefreshAsync()).Result.Reason.Should().Be(PlayableMatchResultReason.Timeout);
    }

    [Fact]
    public async Task PlayControlledMove_WhenSearchExceedsClock_RejectsMove()
    {
        await using var match = await Match.CreateAsync(clocked: true, whiteEngine: true);
        match.PlayingTransport.When(t => t.WriteLineAsync(Arg.Is<string>(s => s.StartsWith("go ")), Arg.Any<CancellationToken>()))
            .Do(_ => { match.Now += TimeSpan.FromSeconds(6); match.PlayingOutput.Writer.TryWrite("bestmove e2e4"); });

        await match.Session.Invoking(s => s.PlayControlledMoveAsync()).Should().ThrowAsync<InvalidOperationException>();
        match.Session.PlayedMoves.Should().BeEmpty();
        (await match.Session.RefreshAsync()).Result.Reason.Should().Be(PlayableMatchResultReason.Timeout);
    }

    [Fact]
    public async Task PlayControlledMove_WhenMatchChangesDuringSearch_RejectsStaleResult()
    {
        await using var match = await Match.CreateAsync(whiteEngine: true);
        match.PlayingTransport.When(t => t.WriteLineAsync(Arg.Is<string>(s => s.StartsWith("go ")), Arg.Any<CancellationToken>()))
            .Do(_ => { match.Session.Resign(); match.PlayingOutput.Writer.TryWrite("bestmove e2e4"); });

        await match.Session.Invoking(s => s.PlayControlledMoveAsync()).Should().ThrowAsync<InvalidOperationException>();
        match.Session.PlayedMoves.Should().BeEmpty();
        match.Session.CurrentState.Result.Reason.Should().Be(PlayableMatchResultReason.Resignation);
    }

    [Fact]
    public async Task Timeout_WhenOpponentHasOnlyKing_IsADrawAndRemainsTerminal()
    {
        await using var match = await Match.CreateAsync(clocked: true);
        await match.Session.LoadMatchAsync(new(Fen.Parse("7k/8/8/8/8/8/8/KR6 w - - 0 1")!.Value));
        match.Now += TimeSpan.FromSeconds(6);
        match.Session.Invoking(s => s.ApplyMove("b1b2")).Should().Throw<InvalidOperationException>();

        match.Session.CurrentState.Result.Reason.Should().Be(PlayableMatchResultReason.Timeout);
        match.Session.CurrentState.Result.IsDraw.Should().BeTrue();
        match.Session.ResumeClock();
        (await match.Session.RefreshAsync()).Result.IsDraw.Should().BeTrue();
    }

    [Fact]
    public async Task MoveEvent_AfterLoadingBlackTurn_UsesFenMoveNumber()
    {
        await using var match = await Match.CreateAsync();
        await match.Session.LoadMatchAsync(new(Fen.Parse("7k/8/8/8/8/8/8/KR6 b - - 0 42")!.Value));
        PlayableMatchMoveData? payload = null;
        match.Session.EventOccurred += e => { if (e.Kind == PlayableMatchEventKind.MoveApplied) payload = e.MoveData; };
        match.Session.ApplyMove("h8g8");
        payload!.Value.MoveNumber.Should().Be(42);
        payload.Value.PlyIndex.Should().Be(0);
    }

    [Fact]
    public void DefaultSetup_ThrowsValidationErrorInsteadOfNullReference()
    {
        var setup = default(PlayableMatchSetup);
        setup.Invoking(s => s.Validate()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TimeControl_DuplicateOrDescendingStageTriggers_AreRejected()
    {
        var duplicate = new PlayableMatchTimeControl(TimeSpan.FromSeconds(5), TimeSpan.Zero,
            additionalStages: [new(2, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero), new(2, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)]);
        duplicate.Invoking(t => t.Validate()).Should().Throw<ArgumentException>();
        var descending = new PlayableMatchTimeControl(TimeSpan.FromSeconds(5), TimeSpan.Zero,
            additionalStages: [new(2, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero), new(1, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero)]);
        descending.Invoking(t => t.Validate()).Should().Throw<ArgumentException>();
    }

    private sealed class Match : IAsyncDisposable
    {
        public DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        public UciPlayableMatchSession Session = null!;
        public IUciTransport PlayingTransport = null!;
        public Channel<string> PlayingOutput = null!;
        private readonly List<UciEngineClient> _clients = [];

        public static async Task<Match> CreateAsync(bool clocked = false, bool whiteEngine = false, bool claimRequired = false)
        {
            var match = new Match();
            for (int i = 0; i < 2; i++)
            {
                var (transport, output) = UciEngineClientTestHelpers.CreateMockTransport();
                transport.When(t => t.WriteLineAsync("isready", Arg.Any<CancellationToken>())).Do(_ => output.Writer.TryWrite("readyok"));
                if (i == 0) (match.PlayingTransport, match.PlayingOutput) = (transport, output);
                match._clients.Add(await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, output));
            }
            match.Session = new(match._clients[0], match._clients[1], 'w',
                whiteEngine ? MatchSideControllerKind.Engine : MatchSideControllerKind.Manual, MatchSideControllerKind.Manual,
                analysisMoveTimeMs: 10, analysisFallbackMoveTimeMs: 10,
                claimableDrawPolicy: claimRequired ? PlayableMatchClaimableDrawPolicy.ClaimRequired : PlayableMatchClaimableDrawPolicy.Automatic,
                timeControl: clocked ? new(TimeSpan.FromSeconds(5), TimeSpan.Zero) : null,
                utcNowProvider: () => match.Now);
            await match.Session.StartNewGameAsync();
            await match.Session.RefreshAsync();
            return match;
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            foreach (var client in _clients) await client.DisposeAsync();
        }
    }
}

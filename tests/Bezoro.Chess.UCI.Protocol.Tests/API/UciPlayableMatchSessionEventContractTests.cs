using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Channels;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.Chess.UCI.Protocol.Tests.Attributes;
using Bezoro.Chess.UCI.Protocol.Tests.Domain;
using FluentAssertions;
using JetBrains.Annotations;
using NSubstitute;

namespace Bezoro.Chess.UCI.Protocol.Tests.API;

[TestSubject(typeof(UciPlayableMatchSession))]
public sealed class UciPlayableMatchSessionEventContractTests
{
	[Fact]
	public async Task ApplyMove_WhenCaptureIsPlayed_ShouldEmitRichMovePayload()
	{
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = CreateManualSession(playingClient, moveListClient);
		var events = new List<PlayableMatchEvent>();
		session.EventOccurred += events.Add;

		await session.LoadMatchAsync(new PlayableMatchSetup(Fen.Parse("7k/8/8/3pP3/8/8/8/K7 w - d6 0 1")!.Value, []), CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);

		session.ApplyMove("e5d6");

		var moveEvent = events.Should().ContainSingle(x => x.Kind == PlayableMatchEventKind.MoveApplied).Subject;
		moveEvent.SchemaVersion.Should().Be(1);
		moveEvent.MoveData.Should().NotBeNull();
		moveEvent.MoveData!.Value.Notation.Should().Be("e5d6");
		moveEvent.MoveData!.Value.From.Should().Be("e5");
		moveEvent.MoveData!.Value.To.Should().Be("d6");
		moveEvent.MoveData!.Value.MovingPiece.Symbol.Should().Be('P');
		moveEvent.MoveData!.Value.CapturedPiece!.Value.Symbol.Should().Be('p');
		moveEvent.MoveData!.Value.Classification.IsCapture.Should().BeTrue();
		moveEvent.MoveData!.Value.Classification.IsEnPassant.Should().BeTrue();
		moveEvent.MoveData!.Value.PreviousFen.Raw.Should().Be("7k/8/8/3pP3/8/8/8/K7 w - d6 0 1");
		moveEvent.MoveData!.Value.ResultingFen.Raw.Should().Be("7k/8/3P4/8/8/8/8/K7 b - - 0 1");
		session.CancelAnalysis();
	}

	[Fact]
	public async Task ChoosePromotion_WhenPromotionCompletes_ShouldEmitEventsInContractOrder()
	{
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = CreateManualSession(playingClient, moveListClient);
		var eventKinds = new List<PlayableMatchEventKind>();
		session.EventOccurred += e => eventKinds.Add(e.Kind);

		await session.LoadMatchAsync(new PlayableMatchSetup(Fen.Parse("1r5k/P7/8/8/8/8/8/K7 w - - 0 1")!.Value, []), CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);

		session.ApplyMove("a7a8");
		session.ChoosePromotion('q');

		eventKinds.Should().ContainInOrder(
			PlayableMatchEventKind.PositionLoaded,
			PlayableMatchEventKind.PositionRefreshed,
			PlayableMatchEventKind.PromotionRequired,
			PlayableMatchEventKind.PromotionChosen,
			PlayableMatchEventKind.MoveApplied
		);
		session.CancelAnalysis();
	}

	[Fact]
	public async Task RefreshAsync_WhenClaimableThreefoldExistsUnderClaimRequiredPolicy_ShouldExposeClaimableResult()
	{
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			perspectiveColor: 'w',
			whiteController: MatchSideControllerKind.Manual,
			blackController: MatchSideControllerKind.Manual,
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 10,
			analysisFallbackMoveTimeMs: 10,
			claimableDrawPolicy: PlayableMatchClaimableDrawPolicy.ClaimRequired
		);

		await session.StartNewGameAsync(CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);

		foreach (var move in new[] { "g1f3", "g8f6", "f3g1", "f6g8", "g1f3", "g8f6", "f3g1", "f6g8" })
		{
			session.ApplyMove(move);
			await session.RefreshAsync(CancellationToken.None);
		}

		session.CurrentState.Result.Should().Be(default(PlayableMatchResult));
		session.CurrentState.ClaimableResult.Should()
			   .Be(new PlayableMatchResult(PlayableMatchResultReason.ThreefoldRepetition, null));
		session.CancelAnalysis();
	}

	[Fact]
	public async Task ProcessAsync_WhenClaimDrawIsRequested_ShouldAdjudicateClaimableDraw()
	{
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			perspectiveColor: 'w',
			whiteController: MatchSideControllerKind.Manual,
			blackController: MatchSideControllerKind.Manual,
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 10,
			analysisFallbackMoveTimeMs: 10,
			claimableDrawPolicy: PlayableMatchClaimableDrawPolicy.ClaimRequired
		);

		await session.StartNewGameAsync(CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);
		foreach (var move in new[] { "g1f3", "g8f6", "f3g1", "f6g8", "g1f3", "g8f6", "f3g1", "f6g8" })
		{
			session.ApplyMove(move);
			await session.RefreshAsync(CancellationToken.None);
		}

		await session.ProcessAsync(new(PlayableMatchRequestKind.ClaimDraw), CancellationToken.None);

		session.CurrentState.Result.Should().Be(new PlayableMatchResult(PlayableMatchResultReason.ThreefoldRepetition, null));
		session.CancelAnalysis();
	}

	[Fact]
	public async Task ProcessAsync_WhenResignAndDrawRequestsAreUsed_ShouldEmitTerminalResults()
	{
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = CreateManualSession(playingClient, moveListClient);
		var events = new List<PlayableMatchEvent>();
		session.EventOccurred += events.Add;

		await session.StartNewGameAsync(CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);
		await session.ProcessAsync(new(PlayableMatchRequestKind.OfferDraw), CancellationToken.None);
		await session.ProcessAsync(new(PlayableMatchRequestKind.AcceptDraw), CancellationToken.None);

		session.CurrentState.Result.Should().Be(new PlayableMatchResult(PlayableMatchResultReason.DrawAgreement, null));
		events.Should().Contain(x => x.Kind == PlayableMatchEventKind.DrawOffered);
		events.Should().Contain(x => x.Kind == PlayableMatchEventKind.ResultChanged && x.Result!.Value.Reason == PlayableMatchResultReason.DrawAgreement);

		await session.StartNewGameAsync(CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);
		await session.ProcessAsync(new(PlayableMatchRequestKind.Resign), CancellationToken.None);

		session.CurrentState.Result.Should().Be(new PlayableMatchResult(PlayableMatchResultReason.Resignation, 'b'));
		session.CancelAnalysis();
	}

	[Fact]
	public async Task ProcessAsync_WhenClockIsPausedAndResumed_ShouldStopAndRestartElapsedTime()
	{
		var startedAt = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
		var now = startedAt;

		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			perspectiveColor: 'w',
			whiteController: MatchSideControllerKind.Manual,
			blackController: MatchSideControllerKind.Manual,
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 10,
			analysisFallbackMoveTimeMs: 10,
			timeControl: new(TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.FromSeconds(3)),
			utcNowProvider: () => now
		);

		await session.StartNewGameAsync(CancellationToken.None);
		var opening = await session.RefreshAsync(CancellationToken.None);
		now = startedAt + TimeSpan.FromSeconds(2);
		await session.ProcessAsync(new(PlayableMatchRequestKind.PauseClock), CancellationToken.None);
		now = startedAt + TimeSpan.FromSeconds(10);
		var paused = await session.RefreshAsync(CancellationToken.None);
		await session.ProcessAsync(new(PlayableMatchRequestKind.ResumeClock), CancellationToken.None);
		now = startedAt + TimeSpan.FromSeconds(12);
		var resumed = await session.RefreshAsync(CancellationToken.None);

		opening.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(30));
		opening.Clock.Value.DelayRemaining.Should().Be(TimeSpan.FromSeconds(3));
		paused.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(30));
		paused.Clock.Value.DelayRemaining.Should().Be(TimeSpan.FromSeconds(1));
		paused.Clock!.Value.IsPaused.Should().BeTrue();
		// Four active seconds consume the three-second delay and one second of main time.
		resumed.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(29));
		resumed.Clock.Value.DelayRemaining.Should().Be(TimeSpan.Zero);
		resumed.Clock.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(30));
		resumed.Clock!.Value.IsPaused.Should().BeFalse();
		session.CancelAnalysis();
	}

	[Fact]
	public async Task PlayUntilTerminalAsync_WhenBothSidesAreEngine_ShouldPlayToCompletion()
	{
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			perspectiveColor: 'w',
			whiteController: MatchSideControllerKind.Engine,
			blackController: MatchSideControllerKind.Engine,
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 10,
			analysisFallbackMoveTimeMs: 10
		);

		await session.LoadMatchAsync(new PlayableMatchSetup(Fen.Parse("7k/5Q2/7K/8/8/8/8/8 w - - 0 1")!.Value, []), CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);

		var terminal = await session.PlayUntilTerminalAsync(maxPlies: 2, CancellationToken.None);

		terminal.Result.Reason.Should().Be(PlayableMatchResultReason.Checkmate);
		terminal.MoveHistory.Should().ContainSingle();
		session.CancelAnalysis();
	}

	[Fact]
	public async Task ApplyMove_WhenDrawOfferPolicyExpiresOnMove_ShouldClearOnlyAfterRecipientsMove()
	{
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			perspectiveColor: 'w',
			whiteController: MatchSideControllerKind.Manual,
			blackController: MatchSideControllerKind.Manual,
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 10,
			analysisFallbackMoveTimeMs: 10,
			drawOfferPolicy: PlayableMatchDrawOfferPolicy.ExpireOnMove
		);

		await session.StartNewGameAsync(CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);

		session.OfferDraw();
		session.ApplyMove("e2e4");
		var refreshed = await session.RefreshAsync(CancellationToken.None);
		refreshed.DrawOfferedBy.Should().Be('w');
		session.ApplyMove("e7e5");
		refreshed = await session.RefreshAsync(CancellationToken.None);

		refreshed.DrawOfferedBy.Should().BeNull();
		session.CancelAnalysis();
	}

	[Fact]
	public async Task ApplyMove_WhenDrawOfferPolicyPersistsUntilResponse_ShouldKeepPendingOffer()
	{
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			perspectiveColor: 'w',
			whiteController: MatchSideControllerKind.Manual,
			blackController: MatchSideControllerKind.Manual,
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 10,
			analysisFallbackMoveTimeMs: 10,
			drawOfferPolicy: PlayableMatchDrawOfferPolicy.PersistUntilResponse
		);

		await session.StartNewGameAsync(CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);

		session.OfferDraw();
		session.ApplyMove("e2e4");
		var refreshed = await session.RefreshAsync(CancellationToken.None);

		refreshed.DrawOfferedBy.Should().Be('w');
		session.CancelAnalysis();
	}

	[Fact]
	public async Task PlayControlledMoveAsync_WhenFallbackPolicyIsThrow_ShouldPropagateSearchFailure()
	{
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			perspectiveColor: 'w',
			whiteController: MatchSideControllerKind.Engine,
			blackController: MatchSideControllerKind.Engine,
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 10,
			analysisFallbackMoveTimeMs: 10,
			controlledMoveFallbackPolicy: PlayableMatchControlledMoveFallbackPolicy.Throw
		);

		await session.StartNewGameAsync(CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);

		await FluentActions.Invoking(() => session.PlayControlledMoveAsync(CancellationToken.None))
						   .Should()
						   .ThrowAsync<InvalidOperationException>()
						   .WithMessage("*bestmove*");
		session.CancelAnalysis();
	}

	[Fact]
	public async Task ProcessBatchAsync_WhenRequestsAreApplied_ShouldReturnOrderedEmittedEvents()
	{
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = CreateManualSession(playingClient, moveListClient);

		var batchEvents = await session.ProcessBatchAsync(
			[
				new(PlayableMatchRequestKind.StartNewGame),
				new(PlayableMatchRequestKind.Refresh),
				new(PlayableMatchRequestKind.ApplyMove, Move: "e2e4"),
				new(PlayableMatchRequestKind.Refresh)
			],
			CancellationToken.None
		);

		batchEvents.Select(x => x.Kind).Should().ContainInOrder(
			PlayableMatchEventKind.GameStarted,
			PlayableMatchEventKind.PositionRefreshed,
			PlayableMatchEventKind.MoveApplied,
			PlayableMatchEventKind.PositionRefreshed
		);
		session.CancelAnalysis();
	}

	[Fact]
	public async Task ApplyMove_WhenMoveMates_ShouldEmitMoveAppliedBeforeResultChanged()
	{
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = CreateManualSession(playingClient, moveListClient);
		var events = new List<PlayableMatchEvent>();
		session.EventOccurred += events.Add;

		await session.LoadMatchAsync(new PlayableMatchSetup(Fen.Parse("7k/5Q2/7K/8/8/8/8/8 w - - 0 1")!.Value, []), CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);

		session.ApplyMove("f7g7");

		events.Select(x => x.Kind).Should().ContainInOrder(
			PlayableMatchEventKind.PositionLoaded,
			PlayableMatchEventKind.PositionRefreshed,
			PlayableMatchEventKind.MoveApplied,
			PlayableMatchEventKind.ResultChanged
		);
		session.CancelAnalysis();
	}

	private static UciPlayableMatchSession CreateManualSession(
		UciEngineClient playingClient,

		UciEngineClient moveListClient)
		=> new(
			playingClient,
			moveListClient,
			perspectiveColor: 'w',
			whiteController: MatchSideControllerKind.Manual,
			blackController: MatchSideControllerKind.Manual,
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 10,
			analysisFallbackMoveTimeMs: 10
		);

	private static async Task<UciEngineClient> CreateStartedClientAsync()
	{
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		ConfigureReadyResponses(transport, channel);
		return await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, channel);
	}

	private static void ConfigureReadyResponses(IUciTransport transport, Channel<string> channel)
	{
		transport.When(x => x.WriteLineAsync("isready", Arg.Any<CancellationToken>()))
				 .Do(_ => channel.Writer.TryWrite("readyok"));
	}
}

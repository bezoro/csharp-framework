using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Channels;
using Bezoro.Chess.UCI.Protocol.Tests.Attributes;
using Bezoro.Chess.UCI.Protocol.Tests.Domain;
using FluentAssertions;
using JetBrains.Annotations;
using NSubstitute;

namespace Bezoro.Chess.UCI.Protocol.Tests.API;

[TestSubject(typeof(UciPlayableMatchSession))]
public sealed class UciPlayableMatchSessionProtocolTests
{
	[Fact]
	public async Task RefreshAsync_WhenEngineDoesNotProvideDisplayBoardOrPerft_ShouldUseLocalBoardState()
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
			analysisFallbackMoveTimeMs: 10
		);

		await session.StartNewGameAsync(CancellationToken.None);

		var state = await session.RefreshAsync(CancellationToken.None);

		state.Fen.Raw.Should().Be(Fen.Default.Raw);
		state.LegalMoves.Should().HaveCount(20);
		state.LegalMoves.Should().Contain(["e2e4", "d2d4", "g1f3"]);
		session.CancelAnalysis();
	}

	[Fact]
	public async Task ApplyMove_WhenPromotionChoiceIsMissing_ShouldRaisePendingPromotionInsteadOfApplyingMove()
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
			analysisFallbackMoveTimeMs: 10
		);
		var events = new List<PlayableMatchEvent>();
		session.EventOccurred += events.Add;

		await session.LoadMatchAsync(new PlayableMatchSetup(Fen.Parse("1r5k/P7/8/8/8/8/8/K7 w - - 0 1")!.Value, []), CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);

		session.ApplyMove("a7a8");

		session.PlayedMoves.Should().BeEmpty();
		session.PendingPromotion.Should().NotBeNull();
		session.PendingPromotion!.Value.MovePrefix.Should().Be("a7a8");
		session.PendingPromotion!.Value.AllowedPromotionPieces.Should().Equal(['q', 'r', 'b', 'n']);
		events.Should().ContainSingle(x => x.Kind == PlayableMatchEventKind.PromotionRequired);
		session.CancelAnalysis();
	}

	[Fact]
	public async Task ChoosePromotion_WhenPendingPromotionExists_ShouldApplyMoveAndRaiseEvents()
	{
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();

		var session = CreateSession(
			playingClient,
			moveListClient
		);
		var events = new List<PlayableMatchEvent>();
		session.EventOccurred += events.Add;

		await session.LoadMatchAsync(new PlayableMatchSetup(Fen.Parse("1r5k/P7/8/8/8/8/8/K7 w - - 0 1")!.Value, []), CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);
		session.ApplyMove("a7a8");

		session.ChoosePromotion('q');
		var state = await session.RefreshAsync(CancellationToken.None);

		session.PendingPromotion.Should().BeNull();
		session.PlayedMoves.Should().Equal(["a7a8q"]);
		state.Fen.Raw.Should().Be("Qr5k/8/8/8/8/8/8/K7 b - - 0 1");
		events.Should().Contain(x => x.Kind == PlayableMatchEventKind.PromotionChosen && x.Move == "a7a8q");
		events.Should().Contain(x => x.Kind == PlayableMatchEventKind.MoveApplied && x.Move == "a7a8q");
		session.CancelAnalysis();
	}

	[Fact]
	public async Task LoadMatchAsync_WhenSetupIncludesMovesAndClock_ShouldReturnRefreshedState()
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
			timeControl: new(
				TimeSpan.FromSeconds(30),
				TimeSpan.FromSeconds(1),
				TimeSpan.FromSeconds(2)));
		var setup = new PlayableMatchSetup(
			Fen.Default,
			["e2e4", "e7e5"],
			new PlayableMatchClockSetup(
				TimeSpan.FromSeconds(25),
				TimeSpan.FromSeconds(20),
				TimeSpan.FromSeconds(2),
				isPaused: true));

		var state = await session.LoadMatchAsync(setup, CancellationToken.None);

		state.Fen.ActiveColor.Should().Be('w');
		state.Clock.Should().NotBeNull();
		state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(25));
		state.Clock.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(20));
		state.Clock.Value.ActiveColor.Should().Be('w');
		state.Clock.Value.DelayRemaining.Should().Be(TimeSpan.FromSeconds(2));
		state.Clock.Value.IsPaused.Should().BeTrue();
		session.PlayedMoves.Should().Equal(["e2e4", "e7e5"]);
		session.CancelAnalysis();
	}

	[Fact]
	public async Task RefreshAsync_WhenPositionIsStalemate_ShouldExposeMatchResult()
	{
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();

		var session = CreateSession(
			playingClient,
			moveListClient
		);

		await session.LoadMatchAsync(new PlayableMatchSetup(Fen.Parse("k7/8/1QK5/8/8/8/8/8 b - - 0 1")!.Value, []), CancellationToken.None);

		var state = await session.RefreshAsync(CancellationToken.None);

		state.Result.Should().Be(new PlayableMatchResult(PlayableMatchResultReason.Stalemate, null));
		state.LegalMoves.Should().BeEmpty();
		session.CancelAnalysis();
	}

	[Fact]
	public async Task RefreshAsync_WhenPositionIsThreefoldRepetition_ShouldExposeDrawResult()
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
			analysisFallbackMoveTimeMs: 10
		);

		await session.StartNewGameAsync(CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);

		foreach (var move in new[] { "g1f3", "g8f6", "f3g1", "f6g8", "g1f3", "g8f6", "f3g1", "f6g8" })
		{
			session.ApplyMove(move);
			await session.RefreshAsync(CancellationToken.None);
		}

		session.CurrentState.Result.Should()
			   .Be(new PlayableMatchResult(PlayableMatchResultReason.ThreefoldRepetition, null));
		session.CancelAnalysis();
	}

	[Fact]
	public async Task RefreshAsync_WhenTimeControlIsEnabled_ShouldExposeRunningClock()
	{
		var times = new Queue<DateTimeOffset>(
		[
			new(2026, 4, 1, 12, 0, 0, TimeSpan.Zero),
			new(2026, 4, 1, 12, 0, 0, TimeSpan.Zero),
			new(2026, 4, 1, 12, 0, 5, TimeSpan.Zero),
			new(2026, 4, 1, 12, 0, 5, TimeSpan.Zero),
			new(2026, 4, 1, 12, 0, 5, TimeSpan.Zero)
		]);

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
			timeControl: new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2)),
			utcNowProvider: () => times.Dequeue()
		);

		await session.StartNewGameAsync(CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);
		session.ApplyMove("e2e4");
		var state = await session.RefreshAsync(CancellationToken.None);

		state.Clock.Should().NotBeNull();
		state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(27));
		state.Clock!.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(30));
		state.Clock!.Value.ActiveColor.Should().Be('b');
		session.CancelAnalysis();
	}

	[Fact]
	public async Task ProcessAsync_WhenCommandsAreApplied_ShouldRouteThroughSerializableRequestModel()
	{
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();

		var session = CreateSession(
			playingClient,
			moveListClient
		);
		var events = new List<PlayableMatchEvent>();
		session.EventOccurred += events.Add;

		await session.ProcessAsync(new(PlayableMatchRequestKind.StartNewGame), CancellationToken.None);
		await session.ProcessAsync(new(PlayableMatchRequestKind.Refresh), CancellationToken.None);
		await session.ProcessAsync(new(PlayableMatchRequestKind.ApplyMove, Move: "e2e4"), CancellationToken.None);
		await session.ProcessAsync(new(PlayableMatchRequestKind.Refresh), CancellationToken.None);
		await session.ProcessAsync(new(PlayableMatchRequestKind.UndoMoves, UndoCount: 1), CancellationToken.None);
		await session.ProcessAsync(new(PlayableMatchRequestKind.Refresh), CancellationToken.None);

		events.Should().Contain(x => x.Kind == PlayableMatchEventKind.GameStarted);
		events.Should().Contain(x => x.Kind == PlayableMatchEventKind.MoveApplied && x.Move == "e2e4");
		events.Should().Contain(x => x.Kind == PlayableMatchEventKind.MovesUndone && x.UndoCount == 1);
		session.PlayedMoves.Should().BeEmpty();
		session.CancelAnalysis();
	}

	private static UciPlayableMatchSession CreateSession(
		UciEngineClient playingClient,

		UciEngineClient moveListClient)
		=> new(
			playingClient,
			moveListClient,
			playerColor: 'w',
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

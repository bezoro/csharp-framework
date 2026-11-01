using Bezoro.Chess.UCI.Protocol.Tests.Domain;
using FluentAssertions;
using JetBrains.Annotations;
using NSubstitute;

namespace Bezoro.Chess.UCI.Protocol.Tests.API;

[TestSubject(typeof(UciPlayableMatchSession))]
public sealed class UciPlayableMatchSessionClockTests
{
	private static readonly PlayableMatchTimeControl TimeControl = new(
		TimeSpan.FromSeconds(30),
		TimeSpan.FromSeconds(1),
		TimeSpan.FromSeconds(2),
		PlayableMatchTimeoutPolicy.AutomaticLoss);
	private static readonly PlayableMatchTimeControl StagedTimeControl = new(
		TimeSpan.FromSeconds(30),
		TimeSpan.FromSeconds(1),
		TimeSpan.FromSeconds(2),
		PlayableMatchTimeoutPolicy.AutomaticLoss,
		[new(1, TimeSpan.Zero, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4))]);

	[Fact]
	public async Task ApplyMove_WhenClockIsPaused_ShouldStartNextTurnPausedAndFrozen()
	{
		var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = CreateSession(playingClient, moveListClient, () => now);
		await session.StartNewGameAsync();
		await session.RefreshAsync();

		now += TimeSpan.FromSeconds(5);
		session.PauseClock();
		now += TimeSpan.FromSeconds(10);
		session.ApplyMove("e2e4");
		now += TimeSpan.FromSeconds(10);
		var state = await session.RefreshAsync();

		state.Clock.Should().NotBeNull();
		state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(28));
		state.Clock.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(30));
		state.Clock.Value.ActiveColor.Should().Be('b');
		state.Clock.Value.DelayRemaining.Should().Be(TimeSpan.FromSeconds(2));
		state.Clock.Value.IsPaused.Should().BeTrue();
		state.Clock.Value.SnapshotUtc.Should().Be(now);
		session.CancelAnalysis();
	}

	[Fact]
	public async Task UndoMoves_WhenRetainedCheckpointIsHistorical_ShouldRestartAtUndoTime()
	{
		var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = CreateSession(playingClient, moveListClient, () => now);
		await session.StartNewGameAsync();
		await session.RefreshAsync();

		now += TimeSpan.FromSeconds(5);
		session.ApplyMove("e2e4");
		await session.RefreshAsync();
		now += TimeSpan.FromSeconds(5);
		session.ApplyMove("e7e5");
		now += TimeSpan.FromSeconds(10);
		session.UndoMoves();
		var state = await session.RefreshAsync();

		state.Clock.Should().NotBeNull();
		state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(28));
		state.Clock.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(30));
		state.Clock.Value.ActiveColor.Should().Be('b');
		state.Clock.Value.DelayRemaining.Should().Be(TimeSpan.FromSeconds(2));
		state.Clock.Value.IsPaused.Should().BeFalse();
		state.Clock.Value.SnapshotUtc.Should().Be(now);
		session.CancelAnalysis();
	}

	[Fact]
	public async Task UndoMoves_WhenCurrentClockIsPaused_ShouldResumeRetainedTurnWithoutFreezing()
	{
		var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = CreateSession(playingClient, moveListClient, () => now);
		await session.StartNewGameAsync();
		await session.RefreshAsync();

		now += TimeSpan.FromSeconds(5);
		session.ApplyMove("e2e4");
		await session.RefreshAsync();
		now += TimeSpan.FromSeconds(3);
		session.PauseClock();
		now += TimeSpan.FromSeconds(12);
		session.ApplyMove("e7e5");
		now += TimeSpan.FromSeconds(5);
		session.UndoMoves();
		var resumedState = await session.RefreshAsync();

		resumedState.Clock.Should().NotBeNull();
		resumedState.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(28));
		resumedState.Clock.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(30));
		resumedState.Clock.Value.ActiveColor.Should().Be('b');
		resumedState.Clock.Value.DelayRemaining.Should().Be(TimeSpan.FromSeconds(2));
		resumedState.Clock.Value.IsPaused.Should().BeFalse();
		resumedState.Clock.Value.SnapshotUtc.Should().Be(now);

		now += TimeSpan.FromSeconds(5);
		var runningState = await session.RefreshAsync();
		runningState.Clock!.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(27));
		runningState.Clock.Value.DelayRemaining.Should().Be(TimeSpan.Zero);
		runningState.Clock.Value.IsPaused.Should().BeFalse();
		runningState.Clock.Value.SnapshotUtc.Should().Be(now);
		session.CancelAnalysis();
	}

	[Fact]
	public async Task UndoMoves_WhenMatchWasLoadedWithMoves_ShouldRestoreLoadedClockCheckpoint()
	{
		var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = CreateSession(
			playingClient,
			moveListClient,
			() => now,
			StagedTimeControl);
		await session.LoadMatchAsync(
			new(
				Fen.Default,
				["e2e4", "e7e5"],
				new(
					TimeSpan.FromSeconds(25),
					TimeSpan.FromSeconds(20),
					TimeSpan.FromSeconds(4),
					false,
					now)));

		now += TimeSpan.FromSeconds(5);
		session.ApplyMove("g1f3");
		now += TimeSpan.FromSeconds(5);
		session.UndoMoves();
		var state = await session.RefreshAsync();

		state.Clock.Should().NotBeNull();
		state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(25));
		state.Clock.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(20));
		state.Clock.Value.ActiveColor.Should().Be('w');
		state.Clock.Value.ActiveStageIndex.Should().Be(1);
		state.Clock.Value.DelayRemaining.Should().Be(TimeSpan.FromSeconds(4));
		state.Clock.Value.IsPaused.Should().BeFalse();
		state.Clock.Value.SnapshotUtc.Should().Be(now);
		session.CancelAnalysis();
	}

	[Fact]
	public async Task UndoMoves_WhenRetainedPositionPredatesClockHistory_ShouldRejectWithoutMutation()
	{
		var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
		await using var playingClient = await CreateStartedClientAsync();

		await using var moveListClient = await CreateStartedClientAsync();
		var session = CreateSession(playingClient, moveListClient, () => now);
		await session.LoadMatchAsync(
			new(
				Fen.Default,
				["e2e4", "e7e5"],
				new(
					TimeSpan.FromSeconds(25),
					TimeSpan.FromSeconds(20),
					TimeSpan.FromSeconds(2),
					false,
					now)));
		var playedMoves = session.PlayedMoves;

		session.CanUndoMoves().Should().BeFalse();
		var act = () => session.UndoMoves();
		act.Should().Throw<InvalidOperationException>().WithMessage("*clock history*");
		session.PlayedMoves.Should().Equal(playedMoves);
		session.CancelAnalysis();
	}

	private static UciPlayableMatchSession CreateSession(
		UciEngineClient               playingClient,

		UciEngineClient               moveListClient,
		Func<DateTimeOffset>          utcNowProvider,
		PlayableMatchTimeControl?     timeControl = null) =>
		new(
			playingClient,
			moveListClient,
			perspectiveColor: 'w',
			whiteController: MatchSideControllerKind.Manual,
			blackController: MatchSideControllerKind.Manual,
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 10,
			analysisFallbackMoveTimeMs: 10,
			timeControl: timeControl ?? TimeControl,
			utcNowProvider: utcNowProvider);

	private static async Task<UciEngineClient> CreateStartedClientAsync()
	{
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		transport.When(x => x.WriteLineAsync("isready", Arg.Any<CancellationToken>()))
			.Do(_ => channel.Writer.TryWrite("readyok"));
		return await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, channel);
	}
}

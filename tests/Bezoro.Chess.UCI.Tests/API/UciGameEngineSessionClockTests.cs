using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Common.Enums;
using Bezoro.Chess.UCI.API.Types;
using Bezoro.Chess.UCI.Protocol;
using Bezoro.Chess.UCI.Protocol.Internal;
using Bezoro.Chess.UCI.Tests.Attributes;
using Bezoro.Chess.UCI.Tests.TestHelpers;
using FluentAssertions;
using JetBrains.Annotations;

namespace Bezoro.Chess.UCI.Tests.API;

[TestSubject(typeof(UciGameEngineSession))]
[Trait("Category", "Integration")]
[Collection("Stockfish")]
public sealed class UciGameEngineSessionClockTests
{
	private static readonly PlayableMatchTimeControl TimeControl = new(
		TimeSpan.FromSeconds(30),
		TimeSpan.FromSeconds(1),
		TimeSpan.FromSeconds(2),
		PlayableMatchTimeoutPolicy.AutomaticLoss);

	[IntegrationTest]
	public async Task MakeMoveAsync_WhenClockTimeIsInjected_ShouldApplyExactDelayDebitAndIncrement()
	{
		var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
		await using var session = CreateSession(() => now);
		await session.StartAsync();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);

		now += TimeSpan.FromSeconds(5);
		var state = await session.MakeMoveAsync("e2e4");

		state.Clock.Should().NotBeNull();
		state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(28));
		state.Clock.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(30));
		state.Clock.Value.ActiveColor.Should().Be('b');
		state.Clock.Value.DelayRemaining.Should().Be(TimeSpan.FromSeconds(2));
		state.Clock.Value.SnapshotUtc.Should().Be(now);
	}

	[IntegrationTest]
	public async Task MakeMoveAsync_WhenReloadAdvancesTime_ShouldChargeLatencyToNextSide()
	{
		var startedAt = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
		var times = new Queue<DateTimeOffset>(
		[
			startedAt,
			startedAt,
			startedAt + TimeSpan.FromSeconds(5),
			startedAt + TimeSpan.FromSeconds(5),
			startedAt + TimeSpan.FromSeconds(8)
		]);
		await using var session = CreateSession(() => times.Dequeue());
		await session.StartAsync();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);

		var state = await session.MakeMoveAsync("e2e4");

		state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(28));
		state.Clock.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(29));
		state.Clock.Value.SnapshotUtc.Should().Be(startedAt + TimeSpan.FromSeconds(8));
		times.Should().BeEmpty();
	}

	[IntegrationTest]
	public async Task MakeMoveAsync_WhenAtDelayBoundaryAndOneTickBeyond_ShouldDebitOnlyExcessAndApplyIncrement()
	{
		var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
		await using var session = CreateSession(() => now);
		await session.StartAsync();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);

		now += TimeSpan.FromSeconds(2);
		var boundaryState = await session.MakeMoveAsync("e2e4");
		boundaryState.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(31));

		now += TimeSpan.FromSeconds(2) + TimeSpan.FromTicks(1);
		var oneTickState = await session.MakeMoveAsync("e7e5");
		oneTickState.Clock!.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(31) - TimeSpan.FromTicks(1));
	}

	[IntegrationTest]
	public async Task OfferDrawAsync_WhenActiveClockExpires_ShouldAwardOpponent()
	{
		var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
		var timeControl = new PlayableMatchTimeControl(TimeSpan.FromSeconds(3), TimeSpan.Zero);
		await using var session = CreateSession(() => now, timeControl);
		await session.StartAsync();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);

		now += TimeSpan.FromSeconds(4);
		var state = await session.OfferDrawAsync();

		state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.Zero);
		state.Result.Reason.Should().Be(PlayableMatchResultReason.Timeout);
		state.Result.Winner.Should().Be('b');
	}

	[IntegrationTest]
	public async Task OfferDrawAsync_WhenTimeoutPolicyIsIgnore_ShouldExposeZeroWithoutEndingMatch()
	{
		var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
		var timeControl = new PlayableMatchTimeControl(
			TimeSpan.FromSeconds(3),
			TimeSpan.Zero,
			timeoutPolicy: PlayableMatchTimeoutPolicy.Ignore);
		await using var session = CreateSession(() => now, timeControl);
		await session.StartAsync();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);

		now += TimeSpan.FromSeconds(4);
		var state = await session.OfferDrawAsync();

		state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.Zero);
		state.Result.IsTerminal.Should().BeFalse();
	}

	[IntegrationTest]
	public async Task OfferDrawAsync_WhenClockIsPaused_ShouldFreezeValuesAtPauseAndUseRequestedSnapshotTime()
	{
		var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
		await using var session = CreateSession(() => now);
		await session.StartAsync();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);

		now += TimeSpan.FromSeconds(5);
		await session.PauseClockAsync();
		now += TimeSpan.FromSeconds(10);
		var state = await session.OfferDrawAsync();

		state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(27));
		state.Clock.Value.DelayRemaining.Should().Be(TimeSpan.Zero);
		state.Clock.Value.IsPaused.Should().BeTrue();
		state.Clock.Value.SnapshotUtc.Should().Be(now);
	}

	[IntegrationTest]
	public async Task LoadMatchAsync_WhenClockSnapshotIsInFuture_ShouldClampElapsedToZero()
	{
		var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
		await using var session = CreateSession(() => now);
		await session.StartAsync();
		var setup = new PlayableMatchSetup(
			Fen.Default,
			clock: new(
				TimeSpan.FromSeconds(20),
				TimeSpan.FromSeconds(18),
				TimeSpan.FromSeconds(2),
				isPaused: false,
				snapshotUtc: now + TimeSpan.FromMinutes(1)));

		var state = await session.LoadMatchAsync(setup);

		state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(20));
		state.Clock.Value.DelayRemaining.Should().Be(TimeSpan.FromSeconds(2));
		state.Clock.Value.SnapshotUtc.Should().Be(now);
	}

	[IntegrationTest]
	public async Task UndoAsync_WhenReturningToTheOpening_ShouldRestoreInitialCheckpointAtInjectedUndoTime()
	{
		var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
		await using var session = CreateSession(() => now);
		await session.StartAsync();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);

		now += TimeSpan.FromSeconds(5);
		await session.MakeMoveAsync("e2e4");
		now += TimeSpan.FromSeconds(7);
		var state = await session.UndoAsync();

		state.Clock.Should().NotBeNull();
		state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(30));
		state.Clock.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(30));
		state.Clock.Value.ActiveColor.Should().Be('w');
		state.Clock.Value.DelayRemaining.Should().Be(TimeSpan.FromSeconds(2));
		state.Clock.Value.SnapshotUtc.Should().Be(now);
	}

	[IntegrationTest]
	public async Task UndoAsync_WhenAdditionalStageWasReached_ShouldRestoreRetainedTimeStageAndMoveCounts()
	{
		var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
		var timeControl = new PlayableMatchTimeControl(
			TimeSpan.FromSeconds(10),
			TimeSpan.Zero,
			additionalStages:
			[
				new(1, TimeSpan.FromSeconds(20), TimeSpan.Zero, TimeSpan.Zero)
			]);
		await using var session = CreateSession(() => now, timeControl);
		await session.StartAsync();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		await session.MakeMoveAsync("e2e4");
		var stagedState = await session.MakeMoveAsync("e7e5");
		stagedState.Clock!.Value.ActiveStageIndex.Should().Be(1);

		var undoneState = await session.UndoAsync();
		undoneState.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(30));
		undoneState.Clock.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(10));
		undoneState.Clock.Value.ActiveStageIndex.Should().Be(0);

		var replayedState = await session.MakeMoveAsync("e7e5");
		replayedState.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(30));
		replayedState.Clock.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(30));
		replayedState.Clock.Value.ActiveStageIndex.Should().Be(1);
	}

	[IntegrationTest]
	public async Task MakeMoveAsync_WhenClockIsPaused_ShouldStartNextTurnPausedAndFrozen()
	{
		var now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
		await using var session = CreateSession(() => now);
		await session.StartAsync();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);

		now += TimeSpan.FromSeconds(5);
		await session.PauseClockAsync();
		now += TimeSpan.FromSeconds(10);
		await session.MakeMoveAsync("e2e4");
		now += TimeSpan.FromSeconds(10);
		var state = await session.OfferDrawAsync();

		state.Clock.Should().NotBeNull();
		state.Clock!.Value.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(28));
		state.Clock.Value.BlackRemaining.Should().Be(TimeSpan.FromSeconds(30));
		state.Clock.Value.ActiveColor.Should().Be('b');
		state.Clock.Value.DelayRemaining.Should().Be(TimeSpan.FromSeconds(2));
		state.Clock.Value.IsPaused.Should().BeTrue();
		state.Clock.Value.SnapshotUtc.Should().Be(now);
	}

	private static UciGameEngineSession CreateSession(
		Func<DateTimeOffset> utcNowProvider,
		PlayableMatchTimeControl? timeControl = null)
	{
		var snapshotClient = new UciEngineClient(TestResourcePaths.STOCKFISH_PATH);
		var ponder = new UciPonderRuntime(new UciEngineClient(TestResourcePaths.STOCKFISH_PATH));
		var classificationClient = new UciEngineClient(TestResourcePaths.STOCKFISH_PATH);
		var options = UciCoordinatorOptions.Default with { TimeControl = timeControl ?? TimeControl };
		return new(snapshotClient, ponder, classificationClient, options: options, utcNowProvider: utcNowProvider);
	}
}

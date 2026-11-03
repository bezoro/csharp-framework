using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Types;

namespace Bezoro.Chess.UCI.Tests.API;

public sealed class UciGameEngineSessionIdentityTests
{
	[Fact]
	public async Task CurrentGameId_ChangesBeforeQueuedPositionEventsAndRemainsStableAcrossMoveAndUndo()
	{
		var context = new QueuedContext();
		await using var session = new UciGameEngineSession(syncContext: context);
		var original = session.CurrentGameId;
		var positions = new List<PositionLoadedEvent>();
		session.PositionLoaded += positions.Add;

		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		var loaded = session.CurrentGameId;
		Assert.NotEqual(Guid.Empty, original);
		Assert.NotEqual(original, loaded);
		Assert.Empty(positions);
		await session.MakeMoveAsync("e2e4");
		await session.UndoAsync();
		Assert.Equal(loaded, session.CurrentGameId);
		context.Drain();
		Assert.Equal(loaded, Assert.Single(positions).GameId);
	}

	[Fact]
	public async Task QueuedTerminalEvents_RetainDifferentMatchIdsEvenWithIdenticalTerminalFenAndHistory()
	{
		var context = new QueuedContext();
		await using var session = new UciGameEngineSession(syncContext: context);
		var finished = new List<GameFinishedEvent>();
		var canonical = new List<UciGameEngineSessionEvent>();
		var legacy = new List<UciState>();
		session.GameFinished += finished.Add;
		session.GameOver += legacy.Add;
		session.EventPublished += entry => { if (entry.Kind == UciGameEngineSessionEventKind.GameFinished) canonical.Add(entry); };

		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		var first = session.CurrentGameId;
		await session.ResignAsync();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		var second = session.CurrentGameId;
		await session.ResignAsync();
		Assert.Empty(finished);
		Assert.NotEqual(first, second);
		context.Drain();

		Assert.Equal(new[] { first, second }, finished.Select(entry => entry.GameId));
		Assert.All(finished, entry => Assert.True(entry.State.Result.IsTerminal));
		Assert.Equal(finished[0].State.CurrentFen.Raw, finished[1].State.CurrentFen.Raw);
		Assert.Equal(finished[0].State.PlayedMoves, finished[1].State.PlayedMoves);
		Assert.Equal(2, legacy.Count);
		Assert.Equal(finished, canonical.Select(entry => entry.GameFinished!.Value));
		Assert.Equal(second, session.CurrentGameId);
	}

	private sealed class QueuedContext : SynchronizationContext
	{
		private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
		public override void Post(SendOrPostCallback callback, object? state) => _queue.Enqueue((callback, state));
		internal void Drain()
		{
			while (_queue.TryDequeue(out var work)) work.Callback(work.State);
		}
	}
}

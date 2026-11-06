using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Common.Enums;
using Bezoro.Chess.UCI.API.Types;
using Bezoro.Chess.UCI.Protocol.Internal;

namespace Bezoro.Chess.UCI.Tests.API;

public sealed class UciGameEngineSessionLocalGameplayTests
{
	[Fact]
	public async Task BishopUnderpromotionFromBFile_AppliesAndPublishesTheFullNotation()
	{
		await using var session = new UciGameEngineSession();
		await session.LoadMatchAsync(new(Fen.Parse("7k/1P5r/8/8/8/8/8/K7 w - - 0 1")!.Value));
		GameMoveEvent? applied = null;
		session.MoveMade += move => applied = move;
		await session.MakeMoveAsync("b7b8b");
		Assert.Equal("b7b8b", Assert.Single(session.PlayedMoves));
		Assert.NotNull(applied);
		Assert.Equal(PieceColor.White, applied.Value.PromotionPiece!.Value.Color);
		Assert.Equal(PieceType.Bishop, applied.Value.PromotionPiece.Value.Type);
	}
	[Fact]
	public async Task NewGame_ImmediatelyRestoresLegalMovesAndClockWithoutAnEngine()
	{
		await using var session = new UciGameEngineSession(options: UciCoordinatorOptions.Default with
		{
			TimeControl = new(TimeSpan.FromMinutes(5), TimeSpan.Zero)
		});
		await session.MakeMoveAsync("e2e4");
		await session.ResignAsync();
		await session.NewGameAsync();
		Assert.False(session.State.IsGameOver);
		Assert.Equal(20, session.LegalMoves.Count);
		Assert.NotNull(session.State.Clock);
		Assert.False(session.State.Clock.Value.IsPaused);
		Assert.Equal('w', session.State.Clock.Value.ActiveColor);
		await session.MakeMoveAsync("d2d4");
		Assert.Equal("d2d4", Assert.Single(session.PlayedMoves));
	}
	private static UciGameEngineSession CreateSession(UciCoordinatorOptions? options = null, Func<DateTimeOffset>? now = null,
		SynchronizationContext? context = null) => new(
		new UciEngineClient("engine-must-never-be-started"),
		new UciPonderRuntime(new UciEngineClient("engine-must-never-be-started")),
		new UciEngineClient("engine-must-never-be-started"),
		syncContext: context, options: options, utcNowProvider: now);

	[Fact]
	public async Task LocalConstructor_AllowsGameplayWithoutConfiguringEnginePath()
	{
		await using var session = new UciGameEngineSession();
		await session.MakeMoveAsync("e2e4");
		Assert.False(session.IsStarted);
		Assert.Equal('b', session.CurrentFen.ActiveColor);
	}

	[Fact]
	public async Task Gameplay_LoadMoveUndoAndReset_RequireNoExternalEngine()
	{
		await using var session = CreateSession();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		Assert.Equal(20, session.LegalMoves.Count);
		var moveTask = session.MakeMoveAsync("e2e4");
		Assert.True(moveTask.IsCompletedSuccessfully);
		var moved = await moveTask;
		Assert.Equal('b', moved.CurrentFen.ActiveColor);
		Assert.Single(moved.PlayedMoves);
		Assert.Null(moved.Evaluation);
		Assert.False(moved.IsSearching);
		Assert.Equal(moved.CurrentFen, await session.GetCurrentFenAsync());
		Assert.Equal(Fen.Default.Raw, (await session.UndoAsync()).CurrentFen.Raw);
		await session.ResetAsync();
		Assert.Equal(Fen.Default.Raw, session.CurrentFen.Raw);
		Assert.False(session.IsStarted);
	}

	[Fact]
	public async Task MakeMove_AfterResignation_IsRejectedAndUndoClearsResult()
	{
		await using var session = CreateSession();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		await session.MakeMoveAsync("e2e4");
		await session.ResignAsync();
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.MakeMoveAsync("e7e5"));
		var state = await session.UndoAsync();
		Assert.False(state.Result.IsTerminal);
		await session.MakeMoveAsync("d2d4");
	}

	[Fact]
	public async Task ConcurrentMoves_AreValidatedAgainstTheCommittedPosition()
	{
		await using var session = CreateSession();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		var attempts = new[] { TryMove("e2e4"), TryMove("d2d4") };
		var outcomes = await Task.WhenAll(attempts);
		Assert.Single(outcomes, static success => success);
		Assert.Single(session.PlayedMoves);

		async Task<bool> TryMove(string notation)
		{
			try { await Task.Run(() => session.MakeMoveAsync(notation)); return true; }
			catch (ArgumentException) { return false; }
		}
	}

	[Fact]
	public async Task LoadMatch_ReconstructsRepetitionsAndUndoHistory()
	{
		await using var session = CreateSession(UciCoordinatorOptions.Default with
		{
			ClaimableDrawPolicy = PlayableMatchClaimableDrawPolicy.ClaimRequired
		});
		string[] cycle = ["g1f3", "g8f6", "f3g1", "f6g8"];
		await session.LoadMatchAsync(new(Fen.Default, cycle.Concat(cycle)));
		Assert.Equal(PlayableMatchResultReason.ThreefoldRepetition, session.State.ClaimableResult?.Reason);
		MoveUndoneEvent? undone = null;
		session.MoveUndone += value => undone = value;
		await session.UndoAsync();
		Assert.Null(session.State.ClaimableResult);
		Assert.NotNull(undone);
		Assert.Single(undone.Value.Moves);
	}

	[Fact]
	public async Task FivefoldRepetition_IsAutomaticAfterClaimableThirdAndFourthOccurrences()
	{
		await using var session = CreateSession(UciCoordinatorOptions.Default with
		{
			ClaimableDrawPolicy = PlayableMatchClaimableDrawPolicy.ClaimRequired
		});
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		string[] cycle = ["g1f3", "g8f6", "f3g1", "f6g8"];
		var history = new List<string>();
		var events = new List<GameMoveEvent>();
		session.MoveMade += events.Add;
		Guid gameId = session.CurrentGameId;
		// The loaded base position is the first occurrence.
		for (int occurrence = 2; occurrence <= 5; occurrence++)
		{
			foreach (string move in cycle)
			{
				await session.MakeMoveAsync(move);
				history.Add(move);
			}
			Assert.Equal(Fen.Default, session.State.BaseFen);
			Assert.Equal(history, session.PlayedMoves);
			Assert.Equal(history, events.Select(move => move.Notation));
			Assert.All(events, move => Assert.Equal(gameId, move.GameId));
			Assert.Equal(Enumerable.Range(1, history.Count), events.Select(move => move.Ply));
			if (occurrence == 5)
			{
				Assert.Equal(PlayableMatchResultReason.FivefoldRepetition, session.State.Result.Reason);
				Assert.True(session.State.IsGameOver);
				Assert.True(session.State.Result.IsDraw);
				Assert.Null(session.State.ClaimableResult);
			}
			else
			{
				Assert.False(session.State.Result.IsTerminal);
				if (occurrence >= 3) Assert.Equal(PlayableMatchResultReason.ThreefoldRepetition, session.State.ClaimableResult?.Reason);
				else Assert.Null(session.State.ClaimableResult);
			}
		}
		var terminal = session.State;
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.MakeMoveAsync("e2e4"));
		Assert.Equal(terminal, session.State);
		var restored = await session.UndoAsync();
		Assert.False(restored.Result.IsTerminal);
		Assert.Equal(PlayableMatchResultReason.ThreefoldRepetition, restored.ClaimableResult?.Reason);
		Assert.Equal(history.Take(history.Count - 1), restored.PlayedMoves);
		Assert.Equal(gameId, session.CurrentGameId);
		await session.MakeMoveAsync(cycle[^1]);
		Assert.Equal(PlayableMatchResultReason.FivefoldRepetition, session.State.Result.Reason);
		Assert.Equal(history, session.PlayedMoves);
	}

	[Fact]
	public async Task LoadMatch_InvalidClockSetup_DoesNotEraseCurrentPositionOrUndoHistory()
	{
		await using var session = CreateSession();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		await session.MakeMoveAsync("e2e4");
		Fen previous = session.CurrentFen;
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.LoadMatchAsync(new(Fen.Default,
			clock: new PlayableMatchClockSetup(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.Zero))));
		Assert.Equal(previous, session.CurrentFen);
		Assert.Equal(Fen.Default.Raw, (await session.UndoAsync()).CurrentFen.Raw);
	}

	[Fact]
	public async Task Promotion_InvalidOrCancelledChoice_PreservesPendingRequest()
	{
		await using var session = CreateSession();
		await session.LoadMatchAsync(new(Fen.Parse("1r5k/P7/8/8/8/8/8/K7 w - - 0 1")!.Value));
		PromotionRequiredEvent? request = null;
		session.PromotionRequired += value => request = value;
		await session.MakeMoveAsync("a7a8");
		Assert.NotNull(request);
		long id = request.Value.PendingPromotionId;
		await Assert.ThrowsAsync<ArgumentException>(() => session.ChoosePromotionAsync(id, PieceType.King));
		using var canceled = new CancellationTokenSource();
		canceled.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ChoosePromotionAsync(id, PieceType.Queen, canceled.Token));
		Assert.Empty(session.PlayedMoves);
		await session.ChoosePromotionAsync(id, PieceType.Queen);
		Assert.Equal("a7a8q", Assert.Single(session.PlayedMoves));
	}

	[Fact]
	public async Task Promotion_ExpiredClock_RejectsChoiceWithoutApplyingMove()
	{
		var now = DateTimeOffset.UtcNow;
		await using var session = CreateSession(UciCoordinatorOptions.Default with
		{
			TimeControl = new(TimeSpan.FromSeconds(1), TimeSpan.Zero)
		}, () => now);
		await session.LoadMatchAsync(new(Fen.Parse("1r5k/P7/8/8/8/8/8/K7 w - - 0 1")!.Value));
		PromotionRequiredEvent? request = null;
		session.PromotionRequired += value => request = value;
		await session.MakeMoveAsync("a7a8");
		now += TimeSpan.FromSeconds(2);
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.ChoosePromotionAsync(request!.Value.PendingPromotionId, PieceType.Queen));
		Assert.Empty(session.PlayedMoves);
	}

	[Fact]
	public async Task StateStream_DoesNotWaitForTheUiSynchronizationContext()
	{
		await using var session = CreateSession(context: new QueuedSynchronizationContext());
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		await using var stream = session.StreamStateAsync().GetAsyncEnumerator();
		Assert.True(await stream.MoveNextAsync());
		await session.OfferDrawAsync();
		Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
		Assert.Equal('w', stream.Current.DrawOfferedBy);
	}

	[Fact]
	public async Task StateStream_CompletesWhenTheSessionIsDisposed()
	{
		var session = CreateSession();
		await using var stream = session.StreamStateAsync().GetAsyncEnumerator();
		Assert.True(await stream.MoveNextAsync());
		await session.DisposeAsync();
		Assert.False(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
		await session.DisposeAsync();
	}

	[Fact]
	public async Task DrawAgreement_RequiresAnOffer()
	{
		await using var session = CreateSession();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.AcceptDrawAsync());
	}

	[Fact]
	public async Task DrawOffer_SurvivesClockPauseAndResume()
	{
		await using var session = CreateSession(UciCoordinatorOptions.Default with
		{
			TimeControl = new(TimeSpan.FromMinutes(5), TimeSpan.Zero)
		});
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		await session.OfferDrawAsync();
		Assert.Equal('w', (await session.PauseClockAsync()).DrawOfferedBy);
		Assert.Equal('w', (await session.ResumeClockAsync()).DrawOfferedBy);
		await session.AcceptDrawAsync();
		Assert.Equal(PlayableMatchResultReason.DrawAgreement, session.State.Result.Reason);
	}

	[Fact]
	public async Task DrawAgreement_CannotOverrideAnExpiredClock()
	{
		var now = DateTimeOffset.UtcNow;
		await using var session = CreateSession(UciCoordinatorOptions.Default with
		{
			TimeControl = new(TimeSpan.FromSeconds(1), TimeSpan.Zero)
		}, () => now);
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		await session.OfferDrawAsync();
		now += TimeSpan.FromSeconds(2);
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.AcceptDrawAsync());
		Assert.Equal(PlayableMatchResultReason.Timeout, session.State.Result.Reason);
	}

	[Fact]
	public async Task LocalConstructor_ExposesConfiguredClockImmediately()
	{
		await using var session = new UciGameEngineSession(options: UciCoordinatorOptions.Default with
		{
			TimeControl = new(TimeSpan.FromMinutes(5), TimeSpan.Zero)
		});
		Assert.NotNull(session.State.Clock);
		Assert.Equal('w', session.State.Clock.Value.ActiveColor);
	}

	private sealed class QueuedSynchronizationContext : SynchronizationContext
	{
		public override void Post(SendOrPostCallback d, object? state) { }
	}
}

using System.Collections.Concurrent;
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Common.Enums;
using Bezoro.Chess.UCI.API.Types;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.UCI.Tests.API;

public sealed class UciGameEngineSessionUndoDetailsTests
{
	[Fact]
	public async Task DetailsCompleteBeforePostedEventsAndReuseTheirCanonicalPayloadArray()
	{
		var context = new QueuedContext();
		await using var session = new UciGameEngineSession(syncContext: context);
		await session.MakeMoveAsync("e2e4");
		await session.MakeMoveAsync("e7e5");
		await session.MakeMoveAsync("g1f3");
		var order = new List<string>();
		MoveUndoneEvent? notification = null;
		session.MoveUndone += value => { notification = value; order.Add("undo"); };
		session.TurnChanged += _ => order.Add("turn");
		session.PositionChanged += _ => order.Add("position");
		session.LegalMovesUpdated += _ => order.Add("legal");

		var result = await session.UndoWithDetailsAsync(2);

		Assert.Null(notification);
		Assert.Empty(order);
		Assert.Equal(new[] { "e2e4" }, result.State.PlayedMoves);
		Assert.Equal(new[] { "e7e5", "g1f3" }, result.UndoneMoves.Select(move => move.Notation));
		Assert.Equal(result.State.CurrentFen, session.CurrentFen);
		context.Drain();
		Assert.NotNull(notification);
		Assert.True(result.UndoneMoves == notification.Value.Moves);
		Assert.Equal(result.State.CurrentFen, notification.Value.ResultingFen);
		Assert.Equal(new[] { "undo", "position", "legal" }, order);

		await session.MakeMoveAsync("c7c5");
		Assert.Equal(new[] { "e2e4" }, result.State.PlayedMoves);
		Assert.Equal("g1f3", result.UndoneMoves[1].Notation);
	}

	[Fact]
	public async Task EmptyHistoryReturnsInitializedEmptyMovesWithoutAnUndoEvent()
	{
		await using var session = new UciGameEngineSession();
		int notifications = 0;
		session.MoveUndone += _ => notifications++;
		var before = session.State;

		var result = await session.UndoWithDetailsAsync();

		Assert.Equal(before, result.State);
		Assert.False(result.UndoneMoves.IsDefault);
		Assert.Empty(result.UndoneMoves);
		Assert.Equal(0, notifications);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(int.MinValue)]
	public async Task NonPositiveCountIsRejectedWithoutChangingHistory(int count)
	{
		await using var session = new UciGameEngineSession();
		await session.MakeMoveAsync("e2e4");
		var before = session.State;

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.UndoWithDetailsAsync(count));

		Assert.Equal(before, session.State);
		Assert.Single((await session.UndoWithDetailsAsync()).UndoneMoves);
	}

	[Fact]
	public async Task CancellationBeforeCommitLeavesHistoryAndEventsUntouched()
	{
		await using var session = new UciGameEngineSession();
		await session.MakeMoveAsync("e2e4");
		int notifications = 0;
		session.MoveUndone += _ => notifications++;
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		var before = session.State;

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.UndoWithDetailsAsync(ct: cancellation.Token));

		Assert.Equal(before, session.State);
		Assert.Equal(0, notifications);
		Assert.Single((await session.UndoWithDetailsAsync()).UndoneMoves);
	}

	[Fact]
	public async Task CountLargerThanHistoryReturnsEveryRemovedMoveInChronologicalOrder()
	{
		await using var session = new UciGameEngineSession();
		await session.LoadMatchAsync(new(Fen.Default, new[] { "e2e4", "e7e5", "g1f3" }));

		var result = await session.UndoWithDetailsAsync(int.MaxValue);

		Assert.Equal(Fen.Default, result.State.CurrentFen);
		Assert.Empty(result.State.PlayedMoves);
		Assert.Equal(new[] { "e2e4", "e7e5", "g1f3" }, result.UndoneMoves.Select(move => move.Notation));
		Assert.Equal(Fen.Default, result.UndoneMoves[0].PreviousFen);
		Assert.All(result.UndoneMoves, move => Assert.Equal(GameMoveActor.Remote, move.Actor));
	}

	[Theory]
	[InlineData("w", "e1g1", "h1", "f1")]
	[InlineData("w", "e1c1", "a1", "d1")]
	[InlineData("b", "e8g8", "h8", "f8")]
	[InlineData("b", "e8c8", "a8", "d8")]
	public async Task CastlingReturnsTheActualSecondaryPieceMotion(string side, string notation, string rookFrom, string rookTo)
	{
		var initial = Fen.Parse($"r3k2r/8/8/8/8/8/8/R3K2R {side} KQkq - 0 1")!.Value;
		await using var session = new UciGameEngineSession();
		await session.LoadMatchAsync(new(initial));
		await session.MakeMoveAsync(notation);
		var movedFen = session.CurrentFen;

		var result = await session.UndoWithDetailsAsync();

		Assert.Equal(initial, result.State.CurrentFen);
		var move = Assert.Single(result.UndoneMoves);
		Assert.Equal(notation, move.Notation);
		Assert.Equal(initial, move.PreviousFen);
		Assert.Equal(movedFen, move.ResultingFen);
		Assert.Equal(PieceType.King, move.MovingPiece.Type);
		Assert.NotNull(move.SecondaryPieceMove);
		Assert.Equal(PieceType.Rook, move.SecondaryPieceMove.Value.Piece.Type);
		Assert.Equal(rookFrom, move.SecondaryPieceMove.Value.From);
		Assert.Equal(rookTo, move.SecondaryPieceMove.Value.To);
	}

	[Theory]
	[InlineData("4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 2", "e5d6", "p", null)]
	[InlineData("4k3/8/8/8/3Pp3/8/8/4K3 b - d3 0 2", "e4d3", "P", null)]
	[InlineData("1r5k/P7/8/8/8/8/8/7K w - - 0 1", "a7b8r", "r", "R")]
	[InlineData("7k/8/8/8/8/8/p7/1R5K b - - 0 1", "a2b1b", "R", "b")]
	[InlineData("1r5k/P7/8/8/8/8/8/7K w - - 0 1", "a7a8q", null, "Q")]
	[InlineData("7k/8/8/8/8/8/p7/1R5K b - - 0 1", "a2a1n", null, "n")]
	public async Task CapturesAndPromotionsRetainTheirOriginalPiecePayloads(string raw, string notation, string? captured, string? promoted)
	{
		var initial = Fen.Parse(raw)!.Value;
		await using var session = new UciGameEngineSession();
		await session.LoadMatchAsync(new(initial));
		await session.MakeMoveAsync(notation);
		var movedFen = session.CurrentFen;

		var result = await session.UndoWithDetailsAsync();

		Assert.Equal(initial, result.State.CurrentFen);
		var move = Assert.Single(result.UndoneMoves);
		Assert.Equal(notation, move.Notation);
		Assert.Equal(PieceType.Pawn, move.MovingPiece.Type);
		Assert.Equal(captured, move.CapturedPiece?.Char.ToString());
		Assert.Equal(promoted, move.PromotionPiece?.Char.ToString());
		Assert.Equal(initial, move.PreviousFen);
		Assert.Equal(movedFen, move.ResultingFen);
		Assert.Null(move.SecondaryPieceMove);
	}

	[Fact]
	public async Task ConcurrentUndosReturnOnlyTheirOwnRemovedMoves()
	{
		await using var session = new UciGameEngineSession();
		await session.LoadMatchAsync(new(Fen.Default, new[] { "e2e4", "e7e5", "g1f3" }));

		var results = await Task.WhenAll(Task.Run(() => session.UndoWithDetailsAsync()), Task.Run(() => session.UndoWithDetailsAsync()));

		Assert.Equal(new[] { "e7e5", "g1f3" }, results.Select(result => Assert.Single(result.UndoneMoves).Notation).OrderBy(value => value));
		Assert.Equal(new[] { 1, 2 }, results.Select(result => result.State.PlayedMoves.Count).OrderBy(value => value));
		Assert.Equal(new[] { "e2e4" }, session.PlayedMoves);
	}

	private sealed class QueuedContext : SynchronizationContext
	{
		private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
		public override void Post(SendOrPostCallback callback, object? state) => _pending.Enqueue((callback, state));
		internal void Drain()
		{
			while (_pending.TryDequeue(out var work)) work.Callback(work.State);
		}
	}
}

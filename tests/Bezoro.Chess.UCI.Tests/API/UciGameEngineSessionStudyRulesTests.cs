using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Types;
using Bezoro.Chess.UCI.Protocol.Internal;
using Bezoro.Chess.UCI.Tests.Domain;
using NSubstitute;

namespace Bezoro.Chess.UCI.Tests.API;

public sealed class UciGameEngineSessionStudyRulesTests
{
	[Fact]
	public async Task Undo_RestoresRetainedClocksInsteadOfResettingTheMatch()
	{
		var now = DateTimeOffset.UtcNow;
		await using var session = new UciGameEngineSession(options: UciCoordinatorOptions.Default with
		{
			TimeControl = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2))
		}, utcNowProvider: () => now);
		now += TimeSpan.FromSeconds(5);
		var first = await session.MakeMoveAsync("e2e4");
		now += TimeSpan.FromSeconds(7);
		await session.MakeMoveAsync("e7e5");
		now += TimeSpan.FromHours(1);
		var retained = await session.UndoAsync();
		Assert.Equal(first.Clock!.Value.WhiteRemaining, retained.Clock!.Value.WhiteRemaining);
		Assert.Equal(first.Clock.Value.BlackRemaining, retained.Clock.Value.BlackRemaining);
		Assert.Equal('b', retained.Clock.Value.ActiveColor);
		Assert.Equal(TimeSpan.FromSeconds(2), retained.Clock.Value.DelayRemaining);
		Assert.False(retained.Clock.Value.IsPaused);
		Assert.Equal(now, retained.Clock.Value.SnapshotUtc);
	}

	[Fact]
	public async Task Undo_CannotInventClockHistoryBeforeTheLoadedPosition()
	{
		await using var session = new UciGameEngineSession(options: UciCoordinatorOptions.Default with
		{
			TimeControl = new(TimeSpan.FromMinutes(5), TimeSpan.Zero)
		});
		await session.LoadMatchAsync(new(Fen.Default, ["e2e4", "e7e5"]));
		var loaded = session.State;
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.UndoAsync());
		Assert.Equal(loaded.CurrentFen, session.CurrentFen);
		await session.MakeMoveAsync("g1f3");
		Assert.Equal(loaded.CurrentFen, (await session.UndoAsync()).CurrentFen);
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.UndoAsync());
	}

	[Fact]
	public async Task DrawOffer_SurvivesTheOfferersMoveAndExpiresWhenTheRecipientMoves()
	{
		await using var session = new UciGameEngineSession();
		await session.OfferDrawAsync('w');
		Assert.Equal('w', (await session.MakeMoveAsync("e2e4")).DrawOfferedBy);
		Assert.Null((await session.MakeMoveAsync("e7e5")).DrawOfferedBy);
	}

	[Fact]
	public async Task DrawResponse_RequiresTheRecipientEvenWhenItIsNotTheirTurn()
	{
		await using var session = new UciGameEngineSession();
		await session.OfferDrawAsync('b');
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.AcceptDrawAsync('b'));
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.DeclineDrawAsync('b'));
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.OfferDrawAsync('w'));
		Assert.Equal(PlayableMatchResultReason.DrawAgreement, (await session.AcceptDrawAsync('w')).Result.Reason);
	}

	[Fact]
	public async Task Resignation_CanBeOffTurnAndDrawsWhenTheOpponentCannotMate()
	{
		await using var session = new UciGameEngineSession();
		await session.LoadMatchAsync(new(Fen.Parse("7k/8/8/8/8/8/8/KR6 w - - 0 1")!.Value));
		var resigned = await session.ResignAsync('w');
		Assert.Equal(PlayableMatchResultReason.Resignation, resigned.Result.Reason);
		Assert.True(resigned.Result.IsDraw);
		await session.NewGameAsync();
		Assert.Equal('w', (await session.ResignAsync('b')).Result.Winner);
	}

	[Theory]
	[InlineData("8/7k/8/8/8/8/8/KR6 w - - 99 1", "b1b2", PlayableMatchResultReason.FiftyMoveRule)]
	public async Task IntendedMoveDrawClaim_DoesNotApplyTheDeclaredMove(string rawFen, string move, PlayableMatchResultReason reason)
	{
		await using var session = new UciGameEngineSession(options: UciCoordinatorOptions.Default with
		{
			ClaimableDrawPolicy = PlayableMatchClaimableDrawPolicy.ClaimRequired
		});
		var initial = Fen.Parse(rawFen)!.Value;
		await session.LoadMatchAsync(new(initial));
		var claimed = await session.ClaimDrawAsync(move);
		Assert.Equal(reason, claimed.Result.Reason);
		Assert.Equal(initial, claimed.CurrentFen);
		Assert.Empty(claimed.PlayedMoves);
	}

	[Fact]
	public async Task IntendedRepetitionClaim_CountsTheDeclaredPositionOnce()
	{
		await using var session = new UciGameEngineSession(options: UciCoordinatorOptions.Default with
		{
			ClaimableDrawPolicy = PlayableMatchClaimableDrawPolicy.ClaimRequired
		});
		string[] prefix = ["g1f3", "g8f6", "f3g1", "f6g8", "g1f3", "g8f6", "f3g1"];
		await session.LoadMatchAsync(new(Fen.Default, prefix));
		var before = session.CurrentFen;
		var claimed = await session.ClaimDrawAsync("f6g8");
		Assert.Equal(PlayableMatchResultReason.ThreefoldRepetition, claimed.Result.Reason);
		Assert.Equal(before, claimed.CurrentFen);
		Assert.Equal(prefix, claimed.PlayedMoves);
	}

	[Fact]
	public async Task CancelPromotion_DoesNotApplyOrUndoAMoveAndRejectsStalePromptIds()
	{
		await using var session = new UciGameEngineSession();
		await session.LoadMatchAsync(new(Fen.Parse("7k/P7/8/8/8/8/8/KR6 w - - 0 1")!.Value));
		long id = 0;
		session.PromotionRequired += value => id = value.PendingPromotionId;
		PromotionCanceledEvent? cancellation = null;
		session.PromotionCanceled += value => cancellation = value;
		await session.MakeMoveAsync("a7a8");
		long previousId = id;
		Assert.True(await session.CancelPendingPromotionAsync(id));
		Assert.NotNull(cancellation);
		Assert.Empty(session.PlayedMoves);
		await session.MakeMoveAsync("a7a8");
		Assert.NotEqual(previousId, id);
		Assert.False(await session.CancelPendingPromotionAsync(previousId));
		Assert.True(await session.CancelPendingPromotionAsync(id));
		Assert.False(await session.CancelPendingPromotionAsync(id));
	}

	[Fact]
	public async Task TerminalResult_InvalidatesOutstandingClassificationWithoutChangingTheBoard()
	{
		await using var session = new UciGameEngineSession();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		int generation = session.AcceptedClassificationGenerationForTests;
		await session.ResignAsync();
		Assert.NotEqual(generation, session.AcceptedClassificationGenerationForTests);
		var stale = new Move("e2e4", MoveAnalysis.Analyze("e2e4", BoardState.FromFen(Fen.Default)!.Value,
			MoveScore.FromCp(25), false));
		session.ApplyClassifiedMoveForTests(stale, generation);
		Assert.Empty(session.State.ClassifiedMoves);
		Assert.Null(session.State.Evaluation);
		Assert.False(session.State.IsSearching);
	}

	[Theory]
	[InlineData("8/7k/8/8/8/8/8/KR6 w - - 150 1", "b1b2")]
	[InlineData("8/7k/8/8/8/8/8/K7 w - - 0 1", "a1a2")]
	public async Task LoadingHistory_CannotContinuePastAnAutomaticTerminalResult(string rawFen, string move)
	{
		await using var session = new UciGameEngineSession();
		await session.MakeMoveAsync("e2e4");
		var before = session.CurrentFen;
		await Assert.ThrowsAsync<ArgumentException>(() => session.LoadMatchAsync(new(Fen.Parse(rawFen)!.Value, [move])));
		Assert.Equal(before, session.CurrentFen);
	}

	[Fact]
	public async Task ExplicitSearch_PreservesTheCompleteHistoryForRepetition()
	{
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		var commands = new List<string>();
		transport.WriteLineAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			string command = (string)call[0];
			commands.Add(command);
			if (command == "uci") channel.Writer.TryWrite("uciok");
			if (command == "isready") channel.Writer.TryWrite("readyok");
			if (command.StartsWith("go ", StringComparison.Ordinal))
			{
				channel.Writer.TryWrite("info depth 1 score cp 0 pv g1f3");
				channel.Writer.TryWrite("bestmove g1f3");
			}
			return Task.CompletedTask;
		});
		var client = new UciEngineClient(transport);
		await client.StartAsync();
		await using var session = new UciGameEngineSession(client,
			new UciPonderRuntime(new UciEngineClient("unused")), new UciEngineClient("unused"));
		await session.LoadMatchAsync(new(Fen.Default, ["g1f3", "g8f6", "f3g1", "f6g8"]));
		await session.SearchAsync(new() { Depth = 1 });
		Assert.Contains("position startpos moves g1f3 g8f6 f3g1 f6g8", commands);
	}
}

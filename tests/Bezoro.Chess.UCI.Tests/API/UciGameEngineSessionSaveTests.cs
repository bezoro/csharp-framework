using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Common.Enums;
using Bezoro.Chess.UCI.API.Types;

namespace Bezoro.Chess.UCI.Tests.API;

public sealed class UciGameEngineSessionSaveTests
{
	[Theory]
	[InlineData("resign")]
	[InlineData("agreement")]
	[InlineData("claim")]
	[InlineData("intended")]
	public async Task TerminalAction_UsesTheValidatedClockInstantEvenIfTheClockCrossesFlagFallDuringCommit(string action)
	{
		var origin = DateTimeOffset.UtcNow;
		var now = origin;
		bool advancing = false;
		DateTimeOffset ReadClock()
		{
			var value = now;
			if (advancing) now += TimeSpan.FromSeconds(2);
			return value;
		}
		var options = UciCoordinatorOptions.Default with
		{
			TimeControl = new(TimeSpan.FromSeconds(3), TimeSpan.Zero),
			ClaimableDrawPolicy = PlayableMatchClaimableDrawPolicy.ClaimRequired
		};
		await using var session = new UciGameEngineSession(options: options, utcNowProvider: ReadClock);
		if (action is "claim" or "intended")
			await session.LoadMatchAsync(new(Fen.Parse($"8/7k/8/8/8/8/8/KR6 w - - {(action == "claim" ? 100 : 99)} 1")!.Value));
		if (action == "agreement") await session.OfferDrawAsync('w');
		now = origin + TimeSpan.FromSeconds(2);
		advancing = true;
		var state = action switch
		{
			"resign" => await session.ResignAsync(),
			"agreement" => await session.AcceptDrawAsync('b'),
			"claim" => await session.ClaimDrawAsync(),
			_ => await session.ClaimDrawAsync("b1b2")
		};
		Assert.Equal(TimeSpan.FromSeconds(1), state.Clock!.Value.WhiteRemaining);
		Assert.True(state.Clock.Value.IsPaused);
		var save = await session.CaptureSaveAsync();
		var restored = await session.LoadSaveAsync(save);
		Assert.Equal(state.Result, restored.Result);
		Assert.Equal(TimeSpan.FromSeconds(1), restored.Clock!.Value.WhiteRemaining);
	}

	[Fact]
	public async Task Saves_AreInteroperableAndPreserveCurrentDebitAndTurnStartUndoClocks()
	{
		var now = DateTimeOffset.UtcNow;
		var control = new PlayableMatchTimeControl(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
		var options = UciCoordinatorOptions.Default with { TimeControl = control };
		await using var source = new UciGameEngineSession(options: options, utcNowProvider: () => now);
		now += TimeSpan.FromSeconds(5);
		await source.MakeMoveAsync("e2e4");
		now += TimeSpan.FromSeconds(3);
		var retained = await source.MakeMoveAsync("e7e5");
		now += TimeSpan.FromSeconds(4);
		await source.MakeMoveAsync("g1f3");
		now += TimeSpan.FromSeconds(9);
		var saved = await source.CaptureSaveAsync();
		Assert.Equal(TimeSpan.FromSeconds(23), saved.Setup.Clock!.Value.ExactRestore!.Value.BlackRemaining);
		Assert.Equal(TimeSpan.FromSeconds(30), saved.ClockHistory[^1].BlackRemaining);
		now += TimeSpan.FromHours(1);
		await using var protocol = new UciPlayableMatchSession(timeControl: control, utcNowProvider: () => now);
		var loaded = await protocol.LoadSaveAsync(saved);
		Assert.Equal(TimeSpan.FromSeconds(23), loaded.Clock!.Value.BlackRemaining);
		var roundtrip = protocol.CaptureSave();
		await using var target = new UciGameEngineSession(options: options, utcNowProvider: () => now);
		await target.LoadSaveAsync(roundtrip);
		var undone = await target.UndoAsync();
		Assert.Equal(retained.CurrentFen, undone.CurrentFen);
		Assert.Equal(retained.Clock!.Value.WhiteRemaining, undone.Clock!.Value.WhiteRemaining);
		Assert.Equal(retained.Clock.Value.BlackRemaining, undone.Clock.Value.BlackRemaining);
		await target.UndoAsync(2);
		Assert.Equal(Fen.Default, target.CurrentFen);
	}

	[Fact]
	public async Task ReloadedPromotion_UsesANewRequestIdAndRemainsPlayable()
	{
		await using var session = new UciGameEngineSession();
		await session.LoadMatchAsync(new(Fen.Parse("7k/P7/8/8/8/8/8/KR6 w - - 0 1")!.Value));
		long requestId = 0;
		session.PromotionRequired += request => requestId = request.PendingPromotionId;
		await session.MakeMoveAsync("a7a8");
		long obsoleteId = requestId;
		var saved = await session.CaptureSaveAsync();
		await session.LoadSaveAsync(saved);
		Assert.NotEqual(obsoleteId, requestId);
		Assert.False(await session.CancelPendingPromotionAsync(obsoleteId));
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.ChoosePromotionAsync(obsoleteId, PieceType.Queen));
		await session.ChoosePromotionAsync(requestId, PieceType.Queen);
		Assert.Equal("a7a8q", Assert.Single(session.PlayedMoves));
	}

	[Fact]
	public async Task InvalidSave_LeavesTheCurrentPositionAndUndoHistoryInPlace()
	{
		await using var session = new UciGameEngineSession();
		await session.MakeMoveAsync("e2e4");
		var saved = await session.CaptureSaveAsync();
		var before = session.CurrentFen;
		await Assert.ThrowsAsync<ArgumentException>(() => session.LoadSaveAsync(saved with
		{
			Result = new(PlayableMatchResultReason.Checkmate, 'w')
		}));
		Assert.Equal(before, session.CurrentFen);
		Assert.Equal(Fen.Default, (await session.UndoAsync()).CurrentFen);
	}

	[Fact]
	public async Task DeclaredDrawMove_IsPreservedWithoutBeingPlayed()
	{
		var options = UciCoordinatorOptions.Default with { ClaimableDrawPolicy = PlayableMatchClaimableDrawPolicy.ClaimRequired };
		await using var session = new UciGameEngineSession(options: options);
		var fen = Fen.Parse("8/7k/8/8/8/8/8/KR6 w - - 99 1")!.Value;
		await session.LoadMatchAsync(new(fen));
		await session.ClaimDrawAsync("b1b2");
		var saved = await session.CaptureSaveAsync();
		Assert.Equal("b1b2", saved.DeclaredDrawMove);
		var restored = await session.LoadSaveAsync(saved);
		Assert.Equal(fen, restored.CurrentFen);
		Assert.Equal(PlayableMatchResultReason.FiftyMoveRule, restored.Result.Reason);
		Assert.Empty(restored.PlayedMoves);
	}
}

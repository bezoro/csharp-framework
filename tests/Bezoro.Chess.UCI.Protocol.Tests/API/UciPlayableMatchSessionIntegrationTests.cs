using Bezoro.Chess.UCI.Protocol.Tests.Attributes;
using Bezoro.Chess.UCI.Protocol.Tests.TestHelpers;
using FluentAssertions;
using JetBrains.Annotations;

namespace Bezoro.Chess.UCI.Protocol.Tests.API;

[Collection("Stockfish")]
[TestSubject(typeof(UciPlayableMatchSession))]
public class UciPlayableMatchSessionIntegrationTests(StockfishFixture fixture)
{
	[IntegrationTest]
	public async Task ApplyHumanMove_WhenOpeningAnalysisExists_ShouldForwardToApplyMoveAndReuseCurrentScore()
	{
		await using var playingClient = new UciEngineClient(fixture.StockfishPath);

		await using var moveListClient = new UciEngineClient(fixture.StockfishPath);
		await Task.WhenAll(
			playingClient.StartAsync(CancellationToken.None),

			moveListClient.StartAsync(CancellationToken.None)
		);

		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			playerColor: 'w',
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 500,
			analysisFallbackMoveTimeMs: 100
		);

		await session.StartNewGameAsync(CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);
		var openingAnalysis = await session.GetLegalMoveAnalysisAsync(CancellationToken.None);
		var e2e4Score = openingAnalysis.Evaluations.Single(static evaluation => evaluation.Move == "e2e4").Score;

		// Deliberately exercise the obsolete compatibility alias's forwarding behavior.
#pragma warning disable CS0618
		session.ApplyHumanMove("e2e4");
#pragma warning restore CS0618
		var afterMove = await session.RefreshAsync(CancellationToken.None);

		afterMove.Advantage.Score.Should().Be(e2e4Score);
		session.MoveHistory.Should().ContainSingle();
		session.TryGetPlayedMoveScore(session.MoveHistory[^1], out var resolvedScore).Should().BeTrue();
		resolvedScore.Should().Be(e2e4Score);
		session.CancelAnalysis();
	}

	[IntegrationTest]
	public async Task PlayEngineMoveAsync_WhenParentAnalysisExists_ShouldForwardAndReuseCurrentScore()
	{
		await using var playingClient = new UciEngineClient(fixture.StockfishPath);

		await using var moveListClient = new UciEngineClient(fixture.StockfishPath);
		await Task.WhenAll(
			playingClient.StartAsync(CancellationToken.None),

			moveListClient.StartAsync(CancellationToken.None)
		);

		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			playerColor: 'w',
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 500,
			analysisFallbackMoveTimeMs: 100
		);

		await session.StartNewGameAsync(CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);
		await session.GetLegalMoveAnalysisAsync(CancellationToken.None);
		session.ApplyMove("e2e4");
		await session.RefreshAsync(CancellationToken.None);
		var afterHumanAnalysis = await session.GetLegalMoveAnalysisAsync(CancellationToken.None);

		// Deliberately exercise the obsolete compatibility alias's forwarding behavior.
#pragma warning disable CS0618
		var engineMove = await session.PlayEngineMoveAsync(CancellationToken.None);
#pragma warning restore CS0618
		var expectedScore = afterHumanAnalysis.Evaluations.Single(evaluation => evaluation.Move == engineMove.Move).Score;
		var afterEngine = await session.RefreshAsync(CancellationToken.None);

		afterEngine.Advantage.Score.Should().Be(expectedScore);
		session.TryGetPlayedMoveScore(session.MoveHistory[^1], out var resolvedScore).Should().BeTrue();
		resolvedScore.Should().Be(expectedScore);
		session.CancelAnalysis();
	}

	[IntegrationTest]
	public async Task WaitForCurrentMoveClassificationsAsync_WhenMateMoveExists_ShouldResolveCheckAndMate()
	{
		await using var playingClient = new UciEngineClient(fixture.StockfishPath);

		await using var moveListClient = new UciEngineClient(fixture.StockfishPath);
		await Task.WhenAll(
			playingClient.StartAsync(CancellationToken.None),

			moveListClient.StartAsync(CancellationToken.None)
		);

		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			playerColor: 'w',
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 500,
			analysisFallbackMoveTimeMs: 100
		);

		await session.LoadMatchAsync(new PlayableMatchSetup(Fen.Parse("7k/5Q2/7K/8/8/8/8/8 w - - 0 1")!.Value, []), CancellationToken.None);

		await session.RefreshAsync(CancellationToken.None);
		var classifications = await session.WaitForCurrentMoveClassificationsAsync(CancellationToken.None);

		classifications["f7g7"].IsCheck.Should().BeTrue();
		classifications["f7g7"].IsMate.Should().BeTrue();
		classifications["f7g7"].IsResolved.Should().BeTrue();
		session.CancelAnalysis();
	}

	[IntegrationTest]
	public async Task WaitForCurrentMoveClassificationsAsync_WhenStalemateMoveExists_ShouldResolveStalemate()
	{
		await using var playingClient = new UciEngineClient(fixture.StockfishPath);

		await using var moveListClient = new UciEngineClient(fixture.StockfishPath);
		await Task.WhenAll(
			playingClient.StartAsync(CancellationToken.None),

			moveListClient.StartAsync(CancellationToken.None)
		);

		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			playerColor: 'w',
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 500,
			analysisFallbackMoveTimeMs: 100
		);

		await session.LoadMatchAsync(new PlayableMatchSetup(Fen.Parse("k7/8/2K5/1Q6/8/8/8/8 w - - 0 1")!.Value, []), CancellationToken.None);

		await session.RefreshAsync(CancellationToken.None);
		var classifications = await session.WaitForCurrentMoveClassificationsAsync(CancellationToken.None);

		classifications["b5b6"].IsStalemate.Should().BeTrue();
		classifications["b5b6"].IsResolved.Should().BeTrue();
		session.CancelAnalysis();
	}

	[IntegrationTest]
	public async Task UndoMoves_WhenLastMoveIsUndone_ShouldRestorePreviousPositionAndTrimHistory()
	{
		await using var playingClient = new UciEngineClient(fixture.StockfishPath);

		await using var moveListClient = new UciEngineClient(fixture.StockfishPath);
		await Task.WhenAll(
			playingClient.StartAsync(CancellationToken.None),

			moveListClient.StartAsync(CancellationToken.None)
		);

		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			playerColor: 'w',
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 500,
			analysisFallbackMoveTimeMs: 100
		);

		await session.StartNewGameAsync(CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);
		session.ApplyMove("e2e4");
		var afterHuman = await session.RefreshAsync(CancellationToken.None);

		await session.PlayControlledMoveAsync(CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);

		session.CanUndoMoves().Should().BeTrue();
		session.UndoMoves();

		session.PlayedMoves.Should().Equal(["e2e4"]);
		session.MoveHistory.Select(static move => move.Move).Should().Equal(["e2e4"]);

		var afterUndo = await session.RefreshAsync(CancellationToken.None);

		afterUndo.PositionKey.Should().Be(afterHuman.PositionKey);
		afterUndo.Advantage.Score.Should().Be(afterHuman.Advantage.Score);
		afterUndo.MoveHistory.Select(static move => move.Move).Should().Equal(["e2e4"]);
		session.CancelAnalysis();
	}

	[IntegrationTest]
	public async Task UndoMoves_WhenPendingUnrefreshedMoveIsUndone_ShouldRestoreCurrentPrefix()
	{
		await using var playingClient = new UciEngineClient(fixture.StockfishPath);

		await using var moveListClient = new UciEngineClient(fixture.StockfishPath);
		await Task.WhenAll(
			playingClient.StartAsync(CancellationToken.None),

			moveListClient.StartAsync(CancellationToken.None)
		);

		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			playerColor: 'w',
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 500,
			analysisFallbackMoveTimeMs: 100
		);

		await session.StartNewGameAsync(CancellationToken.None);
		var opening = await session.RefreshAsync(CancellationToken.None);

		session.ApplyMove("e2e4");
		session.CanUndoMoves().Should().BeTrue();

		session.UndoMoves();

		session.PlayedMoves.Should().BeEmpty();
		session.MoveHistory.Should().BeEmpty();

		var afterUndo = await session.RefreshAsync(CancellationToken.None);

		afterUndo.PositionKey.Should().Be(opening.PositionKey);
		afterUndo.LegalMoves.Should().BeEquivalentTo(opening.LegalMoves);
		session.CancelAnalysis();
	}

	[IntegrationTest]
	public async Task UndoMoves_WhenMultipleMovesAreUndone_ShouldRestoreEarlierPrefix()
	{
		await using var playingClient = new UciEngineClient(fixture.StockfishPath);

		await using var moveListClient = new UciEngineClient(fixture.StockfishPath);
		await Task.WhenAll(
			playingClient.StartAsync(CancellationToken.None),

			moveListClient.StartAsync(CancellationToken.None)
		);

		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			playerColor: 'w',
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 500,
			analysisFallbackMoveTimeMs: 100
		);

		await session.StartNewGameAsync(CancellationToken.None);
		var opening = await session.RefreshAsync(CancellationToken.None);

		session.ApplyMove("e2e4");
		await session.RefreshAsync(CancellationToken.None);
		await session.PlayControlledMoveAsync(CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);
		session.ApplyMove("g1f3");
		await session.RefreshAsync(CancellationToken.None);

		session.CanUndoMoves(3).Should().BeTrue();
		session.UndoMoves(3);

		session.PlayedMoves.Should().BeEmpty();
		session.MoveHistory.Should().BeEmpty();
		session.CanUndoMoves().Should().BeFalse();

		var afterUndo = await session.RefreshAsync(CancellationToken.None);

		afterUndo.PositionKey.Should().Be(opening.PositionKey);
		afterUndo.LegalMoves.Should().BeEquivalentTo(opening.LegalMoves);
		afterUndo.MoveHistory.Should().BeEmpty();
		session.CancelAnalysis();
	}

	[IntegrationTest]
	public async Task PlayControlledMoveAsync_WhenHumanChoosesBlack_ShouldLetEnginePlayWhiteFirst()
	{
		await using var playingClient = new UciEngineClient(fixture.StockfishPath);

		await using var moveListClient = new UciEngineClient(fixture.StockfishPath);
		await Task.WhenAll(
			playingClient.StartAsync(CancellationToken.None),

			moveListClient.StartAsync(CancellationToken.None)
		);

		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			perspectiveColor: 'b',
			whiteController: MatchSideControllerKind.Engine,
			blackController: MatchSideControllerKind.Manual,
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 500,
			analysisFallbackMoveTimeMs: 100
		);

		session.WhiteController.Should().Be(MatchSideControllerKind.Engine);
		session.BlackController.Should().Be(MatchSideControllerKind.Manual);
		session.PerspectiveColor.Should().Be('b');

		await session.StartNewGameAsync(CancellationToken.None);
		var opening = await session.RefreshAsync(CancellationToken.None);
		opening.Fen.ActiveColor.Should().Be('w');

		var engineMove = await session.PlayControlledMoveAsync(CancellationToken.None);
		engineMove.Move.Should().NotBeNullOrWhiteSpace();

		var afterEngine = await session.RefreshAsync(CancellationToken.None);
		afterEngine.Fen.ActiveColor.Should().Be('b');
		afterEngine.MoveHistory.Should().ContainSingle();
		session.CancelAnalysis();
	}

	[IntegrationTest]
	public async Task ApplyMove_WhenBothSidesAreManual_ShouldAllowHumanVsHumanPlay()
	{
		await using var playingClient = new UciEngineClient(fixture.StockfishPath);

		await using var moveListClient = new UciEngineClient(fixture.StockfishPath);
		await Task.WhenAll(
			playingClient.StartAsync(CancellationToken.None),

			moveListClient.StartAsync(CancellationToken.None)
		);

		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			perspectiveColor: 'w',
			whiteController: MatchSideControllerKind.Manual,
			blackController: MatchSideControllerKind.Manual,
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 500,
			analysisFallbackMoveTimeMs: 100
		);

		await session.StartNewGameAsync(CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);

		session.ApplyMove("e2e4");
		var afterWhite = await session.RefreshAsync(CancellationToken.None);
		afterWhite.Fen.ActiveColor.Should().Be('b');

		session.ApplyMove("e7e5");
		var afterBlack = await session.RefreshAsync(CancellationToken.None);
		afterBlack.Fen.ActiveColor.Should().Be('w');
		afterBlack.MoveHistory.Select(static move => move.Move).Should().Equal(["e2e4", "e7e5"]);
		session.CancelAnalysis();
	}

	[IntegrationTest]
	public async Task PlayControlledMoveAsync_WhenBothSidesAreEngine_ShouldAllowEngineVsEnginePlay()
	{
		await using var playingClient = new UciEngineClient(fixture.StockfishPath);

		await using var moveListClient = new UciEngineClient(fixture.StockfishPath);
		await Task.WhenAll(
			playingClient.StartAsync(CancellationToken.None),

			moveListClient.StartAsync(CancellationToken.None)
		);

		var session = new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			perspectiveColor: 'w',
			whiteController: MatchSideControllerKind.Engine,
			blackController: MatchSideControllerKind.Engine,
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 500,
			analysisFallbackMoveTimeMs: 100
		);

		await session.StartNewGameAsync(CancellationToken.None);
		await session.RefreshAsync(CancellationToken.None);

		await session.PlayControlledMoveAsync(CancellationToken.None);
		var afterWhite = await session.RefreshAsync(CancellationToken.None);
		afterWhite.Fen.ActiveColor.Should().Be('b');

		await session.PlayControlledMoveAsync(CancellationToken.None);
		var afterBlack = await session.RefreshAsync(CancellationToken.None);
		afterBlack.Fen.ActiveColor.Should().Be('w');
		afterBlack.MoveHistory.Should().HaveCount(2);
		session.CancelAnalysis();
	}

	[Fact]
	public void UndoMoves_WhenCountIsNotPositive_ShouldThrowArgumentOutOfRangeException()
	{
		var session = CreateUnstartedSession();

		session.Invoking(static value => value.UndoMoves(0))
			   .Should()
			   .Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void UndoMoves_WhenMoreMovesThanPlayedAreRequested_ShouldThrowInvalidOperationException()
	{
		var session = CreateUnstartedSession();

		session.Invoking(static value => value.UndoMoves())
			   .Should()
			   .Throw<InvalidOperationException>();
	}

	[Fact]
	public void CanUndoMoves_WhenRequestedCountExceedsPlayedMoves_ShouldReturnFalse()
	{
		var session = CreateUnstartedSession();

		session.CanUndoMoves().Should().BeFalse();
		session.CanUndoMoves(2).Should().BeFalse();
	}

	private UciPlayableMatchSession CreateUnstartedSession()
	{
		var playingClient = new UciEngineClient(fixture.StockfishPath);

		var moveListClient = new UciEngineClient(fixture.StockfishPath);

		return new UciPlayableMatchSession(
			playingClient,
			moveListClient,
			playerColor: 'w',
			engineMoveTimeMs: 100,
			analysisMoveTimeMs: 500,
			analysisFallbackMoveTimeMs: 100
		);
	}
}

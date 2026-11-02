using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.Tests.TestHelpers;

namespace Bezoro.Chess.UCI.Protocol.Tests.API;

[Trait("Category", "Integration")]
[Collection("Stockfish")]
public sealed class UciGameReviewStockfishTests
{
	[Fact]
	public async Task RealStockfishReview_ProducesExactWdlEvidenceForEveryNonterminalPosition()
	{
		await using var client = new UciEngineClient(TestResourcePaths.STOCKFISH_PATH);
		await client.StartAsync();
		var analyzer = new UciGameReviewAnalyzer(client, new(new() { Nodes = 3_000 }));
		var result = await analyzer.AnalyzeAsync(Fen.Default, ["e2e4", "e7e5", "g1f3"]);
		Assert.Equal(4, result.Positions.Length);
		Assert.Equal(3, result.Moves.Length);
		foreach (var position in result.Positions)
		{
			Assert.False(position.IsTerminal);
			Assert.NotNull(position.PrincipalVariation);
			Assert.Equal(UciScoreBound.Exact, position.PrincipalVariation.Value.ScoreBound);
			Assert.True(position.PrincipalVariation.Value.Wdl!.Value.IsValid);
			Assert.Contains(position.BestMove, position.Position.GetLegalMoves());
		}
		foreach (var move in result.Moves)
		{
			Assert.InRange(move.ExpectedPointsBefore, 0, 1);
			Assert.InRange(move.ExpectedPointsAfter, 0, 1);
			Assert.InRange(move.ExpectedPointsLoss, 0, 1);
		}
		Assert.InRange(result.WhiteAccuracy!.Value, 0, 100);
		Assert.InRange(result.BlackAccuracy!.Value, 0, 100);
	}

	[Fact]
	public async Task CancellingARealSearch_ReleasesTheEngineForTheNextReview()
	{
		await using var client = new UciEngineClient(TestResourcePaths.STOCKFISH_PATH);
		await client.StartAsync();
		var analyzer = new UciGameReviewAnalyzer(client, new(new() { Nodes = 100_000 }));
		using var cancellation = new CancellationTokenSource();
		int received = 0;
		void CancelAfterFirstVariation(PrincipalVariation _)
		{
			if (Interlocked.Exchange(ref received, 1) == 0) cancellation.Cancel();
		}
		client.InfoPvReceived += CancelAfterFirstVariation;
		try
		{
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
				analyzer.AnalyzeAsync(Fen.Default, ["e2e4"], ct: cancellation.Token));
		}
		finally { client.InfoPvReceived -= CancelAfterFirstVariation; }
		Assert.Equal(1, received);
		var result = await analyzer.AnalyzeAsync(Fen.Default, []);
		Assert.Single(result.Positions);
		Assert.Empty(result.Moves);
		Assert.True(result.Positions[0].PrincipalVariation!.Value.Wdl!.Value.IsValid);
	}
}

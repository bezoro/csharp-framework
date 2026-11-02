using System.Collections.Immutable;
using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Xunit;

namespace Bezoro.Chess.Networking.Tests;

[Trait("Category", "Integration")]
public sealed class UciChessServiceIntegrationTests
{
    [Fact]
    public async Task DedicatedStockfishServiceSerializesPlayingAndSeparateReviewerEvaluatesStudy()
    {
        string enginePath = Environment.GetEnvironmentVariable("BEZORO_STOCKFISH_PATH") ??
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../Bezoro.Chess.UCI.Protocol.Tests/TestResources/Engine/stockfish/stockfish-windows-x86-64-avx2.exe"));
        Assert.True(File.Exists(enginePath), $"Set BEZORO_STOCKFISH_PATH to a Stockfish executable. Missing: {enginePath}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = new UciEngineClient(enginePath);
        await client.StartAsync(timeout.Token);
        var moveService = new UciChessMoveService(client);
        var firstPosition = new ChessEnginePosition(Guid.NewGuid(), 0, Fen.Default, ImmutableArray<string>.Empty);
        var secondPosition = new ChessEnginePosition(Guid.NewGuid(), 1, Fen.Default, ["e2e4"]);
        var moves = await Task.WhenAll(
            moveService.FindMoveAsync(firstPosition, new() { Nodes = 1000 }, timeout.Token),
            moveService.FindMoveAsync(secondPosition, new() { Nodes = 1000 }, timeout.Token));
        Assert.Contains(moves[0], Fen.Default.GetLegalMoves());
        Assert.Contains(moves[1], Fen.Default.ApplyMove("e2e4").GetLegalMoves());

        // The move-service work is finished before this dedicated client's ownership is handed to the reviewer.
        var reviewService = new UciChessReviewService(new(client, new(new() { Depth = 4 })));
        var review = await reviewService.ReviewAsync(secondPosition, ct: timeout.Token);
        Assert.Equal(2, review.Positions.Length);
        Assert.Single(review.Moves);
        Assert.Equal("e2e4", review.Moves[0].Move);
        Assert.NotNull(review.WhiteAccuracy);
    }
}

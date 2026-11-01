using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.Tests.Domain;
using FluentAssertions;
using NSubstitute;

namespace Bezoro.Chess.UCI.Protocol.Tests.API;

public sealed class UciPositionAnalysisCoordinatorRobustnessTests
{
    [Fact]
    public async Task CompletedCache_EvictsOldestPositionAtConfiguredCapacity()
    {
        await using var coordinator = new UciPositionAnalysisCoordinator(
            (_, _) => Task.FromResult(new PositionAnalysisResult(default, [])), maxCachedPositions: 2);
        foreach (string key in new[] { "first", "second", "third" })
        {
            coordinator.Enqueue(key, [], 'w', 'w', []);
            await coordinator.GetAnalysisAsync(key);
        }
        coordinator.TryGetAnalysis("first", out _).Should().BeFalse();
        coordinator.TryGetAnalysis("second", out _).Should().BeTrue();
        coordinator.TryGetAnalysis("third", out _).Should().BeTrue();
    }

    [Fact]
    public async Task PendingBudget_RejectsAnotherPositionWithoutCancelingAcceptedWork()
    {
        var release = new TaskCompletionSource<PositionAnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new UciPositionAnalysisCoordinator((_, _) => release.Task, maxPendingPositions: 1);
        coordinator.Enqueue("first", [], 'w', 'w', []);
        var accepted = coordinator.GetAnalysisAsync("first");
        Action enqueue = () => coordinator.Enqueue("second", [], 'w', 'w', []);
        enqueue.Should().Throw<InvalidOperationException>().WithMessage("*pending-position limit*");
        accepted.IsCompleted.Should().BeFalse();
        release.SetResult(new(default, []));
        await accepted;
        coordinator.TryGetAnalysis("second", out _).Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_CancelsWaitersAndWaitsForRetiredWorker()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<PositionAnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new UciPositionAnalysisCoordinator((_, _) => { entered.SetResult(); return release.Task; });
        coordinator.Enqueue("first", [], 'w', 'w', []);
        var waiting = coordinator.GetAnalysisAsync("first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposing = coordinator.DisposeAsync().AsTask();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            disposing.IsCompleted.Should().BeFalse();
        }
        finally { release.TrySetResult(new(default, [])); }
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task ExplicitBasePosition_IsSentWithFullMoveHistory()
    {
        var (transport, output) = UciEngineClientTestHelpers.CreateMockTransport();
        transport.When(t => t.WriteLineAsync("isready", Arg.Any<CancellationToken>()))
            .Do(_ => output.Writer.TryWrite("readyok"));
        await using var client = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, output);
        using var coordinator = new UciPositionAnalysisCoordinator(client);
        var baseFen = Fen.Parse("7k/8/8/8/8/8/8/KR6 w - - 0 42")!.Value;
        var resultingFen = baseFen.ApplyMove("b1b2");
        coordinator.Enqueue(resultingFen.Raw, baseFen, ["b1b2"], 'b', 'w', []);

        await coordinator.GetAnalysisAsync(resultingFen.Raw).WaitAsync(TimeSpan.FromSeconds(5));

        await transport.Received().WriteLineAsync($"position fen {baseFen.Raw} moves b1b2", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SamePositionKey_WithDifferentPerspective_InvalidatesCompletedCache()
    {
        var calls = 0;
        using var coordinator = new UciPositionAnalysisCoordinator((item, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new PositionAnalysisResult(
                PositionAdvantage.FromScore(new(item.PlayerColor == 'w' ? 50 : -50, null)), []));
        });
        coordinator.Enqueue("same", [], 'w', 'w', []);
        var first = await coordinator.GetAnalysisAsync("same");
        coordinator.Enqueue("same", [], 'w', 'b', []);
        var second = await coordinator.GetAnalysisAsync("same");

        calls.Should().Be(2);
        first.Advantage.Score.Cp.Should().Be(50);
        second.Advantage.Score.Cp.Should().Be(-50);
    }

    [Fact]
    public async Task SamePositionKey_WithChangedHistory_RejectsOldInFlightResult()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new UciPositionAnalysisCoordinator(async (item, _) =>
        {
            if (item.Moves.Length == 0)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
            return new PositionAnalysisResult(PositionAdvantage.FromScore(new(item.Moves.Length, null)), []);
        });
        coordinator.Enqueue("same", [], 'w', 'w', []);
        var oldWait = coordinator.GetAnalysisAsync("same");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Enqueue("same", ["g1f3", "g8f6", "f3g1", "f6g8"], 'w', 'w', []);
        var currentWait = coordinator.GetAnalysisAsync("same");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldWait);
        releaseFirst.TrySetResult();

        (await currentWait.WaitAsync(TimeSpan.FromSeconds(5))).Advantage.Score.Cp.Should().Be(4);
        coordinator.TryGetAnalysis("same", out var cached).Should().BeTrue();
        cached.Advantage.Score.Cp.Should().Be(4);
    }

    [Fact]
    public async Task CancelingOneWait_DoesNotCancelSharedAnalysis()
    {
        var release = new TaskCompletionSource<PositionAnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new UciPositionAnalysisCoordinator((_, _) => release.Task);
        coordinator.Enqueue("position", [], 'w', 'w', []);
        using var cancellation = new CancellationTokenSource();
        var canceledWait = coordinator.GetAnalysisAsync("position", cancellation.Token);
        var sharedWait = coordinator.GetAnalysisAsync("position");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
        sharedWait.IsCompleted.Should().BeFalse();

        var expected = new PositionAnalysisResult(default, []);
        release.SetResult(expected);
        (await sharedWait.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(expected);
    }
}

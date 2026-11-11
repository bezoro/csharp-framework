using System.Collections.Immutable;
using Bezoro.Chess.UCI.Protocol.API;

namespace Bezoro.Chess.UCI.Protocol.Tests.API;

public sealed class UciPositionAnalysisCoordinatorCancellationTests
{
    [Fact]
    public async Task CancellationCallbacksCanJoinConcurrentReadsAndObserveRetiredWaiters()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UciPositionAnalysisCoordinator? coordinator = null;
        Task<PositionAnalysisResult>? waiter = null;
        bool callbackJoined = false, waiterWasCanceled = false;
        Task<bool>? callbackRead = null;
        coordinator = new(async (request, token) =>
        {
            using var registration = token.Register(() =>
            {
                waiterWasCanceled = waiter!.IsCanceled;
                callbackRead = Task.Run(() => coordinator!.TryGetAnalysis("retired", out _));
                callbackJoined = callbackRead.Wait(TimeSpan.FromSeconds(5));
            });
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return EmptyAnalysis;
        });
        try
        {
            coordinator.Enqueue("retired", [], 'w', 'w', []);
            waiter = coordinator.GetAnalysisAsync("retired");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            coordinator.Cancel();

            Assert.True(waiterWasCanceled);
            Assert.True(callbackJoined);
            Assert.False(await callbackRead!.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        }
        finally { release.TrySetResult(); await coordinator.DisposeAsync(); }
    }

    [Fact]
    public async Task ThrowingCancellationCallbacksStillJoinTheWorkerAndEveryDisposalCallerSeesTheFailure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFailure = new InvalidOperationException("expected cancellation callback failure");
        var coordinator = new UciPositionAnalysisCoordinator(async (request, token) =>
        {
            using var registration = token.Register(() => throw callbackFailure);
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return EmptyAnalysis;
        });
        Task? first = null, second = null;
        try
        {
            coordinator.Enqueue("retired", [], 'w', 'w', []);
            var waiter = coordinator.GetAnalysisAsync("retired");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            first = coordinator.DisposeAsync().AsTask();
            second = coordinator.DisposeAsync().AsTask();

            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.True(waiter.IsCanceled);
            release.TrySetResult();
            var firstFailure = await Assert.ThrowsAsync<AggregateException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
            var secondFailure = await Assert.ThrowsAsync<AggregateException>(() => second.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains(callbackFailure, firstFailure.InnerExceptions);
            Assert.Contains(callbackFailure, secondFailure.InnerExceptions);
        }
        finally
        {
            release.TrySetResult();
            if (first is not null) try { await first.WaitAsync(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            if (second is not null) try { await second.WaitAsync(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task PreparedDisposalInvalidatesAdmissionImmediatelyAndAsyncDisposalWaitsForItsCompletionAction()
    {
        var coordinator = new UciPositionAnalysisCoordinator((_, _) => Task.FromResult(EmptyAnalysis));
        Action? complete = coordinator.BeginCancellation(dispose: true);
        Assert.NotNull(complete);
        Assert.Null(coordinator.BeginCancellation(dispose: true));
        Assert.Throws<ObjectDisposedException>(() => coordinator.Enqueue("new", [], 'w', 'w', []));
        var disposal = coordinator.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposal.IsCompleted);
        }
        finally { complete(); }
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReentrantCancellationCallbackCanAdmitReplacementWorkWithoutLosingItsWaiter()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UciPositionAnalysisCoordinator? coordinator = null;
        Task<PositionAnalysisResult>? replacement = null;
        int calls = 0;
        coordinator = new(async (request, token) =>
        {
            if (Interlocked.Increment(ref calls) != 1) return EmptyAnalysis;
            using var registration = token.Register(() =>
            {
                coordinator!.Enqueue("same", ["e2e4"], 'b', 'w', []);
                replacement = coordinator.GetAnalysisAsync("same");
            });
            entered.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return EmptyAnalysis;
        });
        try
        {
            coordinator.Enqueue("same", [], 'w', 'w', []);
            var retired = coordinator.GetAnalysisAsync("same");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            coordinator.Cancel();
            Assert.True(retired.IsCanceled);
            Assert.NotNull(replacement);
            Assert.False(replacement.IsCompleted);
            release.TrySetResult();
            await replacement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, calls);
        }
        finally { release.TrySetResult(); await coordinator.DisposeAsync(); }
    }

    private static PositionAnalysisResult EmptyAnalysis => new(PositionAdvantage.Pending(), ImmutableArray<MoveEvaluation>.Empty);
}

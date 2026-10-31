using System.Collections.Immutable;
using Bezoro.Chess.UCI.Protocol.Internal;
using FluentAssertions;
using JetBrains.Annotations;

namespace Bezoro.Chess.UCI.Protocol.Tests.API;

[TestSubject(typeof(UciPositionAnalysisCoordinator))]
public class UciPositionAnalysisCoordinatorTests
{
	[Fact]
	public async Task Cancel_WhenGenerationIsDisposedAfterDequeue_ShouldContinueWithReplacement()
	{
		using var workDequeued = new ManualResetEventSlim();
		using var releaseDequeuedWork = new ManualResetEventSlim();
		var dequeueCalls = 0;
		var analysisCalls = 0;
		using var coordinator = new UciPositionAnalysisCoordinator(
			(_, _) => Task.FromResult(Result(Interlocked.Increment(ref analysisCalls))),
			afterWorkDequeued: () =>
			{
				if (Interlocked.Increment(ref dequeueCalls) != 1)
					return;

				workDequeued.Set();
				releaseDequeuedWork.Wait();
			}
		);

		Enqueue(coordinator, "position");
		try
		{
			workDequeued.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
			var retiredWaiter = coordinator.GetAnalysisAsync("position");
			var serializer = coordinator.ActiveWorker;

			coordinator.Cancel();
			Enqueue(coordinator, "position");
			coordinator.ActiveWorker.Should().BeSameAs(serializer);
			releaseDequeuedWork.Set();

			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retiredWaiter);
			(await coordinator.GetAnalysisAsync("position").WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(Result(2));
			await serializer!.WaitAsync(TimeSpan.FromSeconds(5));
			analysisCalls.Should().Be(2);
		}
		finally
		{
			releaseDequeuedWork.Set();
		}
	}

	[Fact]
	public async Task Cancel_WhenRetiredAnalysisFaultsAfterSameKeyReplacement_ShouldSerializeAndIgnoreStaleFault()
	{
		var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var calls = 0;
		using var coordinator = new UciPositionAnalysisCoordinator(
			async (_, _) =>
			{
				if (Interlocked.Increment(ref calls) == 1)
				{
					firstStarted.SetResult();
					await releaseFirst.Task;
					throw new InvalidOperationException("retired failure");
				}

				secondStarted.SetResult();
				await releaseSecond.Task;
				return Result(2);
			}
		);

		try
		{
			Enqueue(coordinator, "position");
			await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			var retiredWaiter = coordinator.GetAnalysisAsync("position");

			coordinator.Cancel();
			Enqueue(coordinator, "position");
			var replacementWaiter = coordinator.GetAnalysisAsync("position");

			releaseFirst.SetResult();
			await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			replacementWaiter.IsCompleted.Should().BeFalse();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retiredWaiter);

			releaseSecond.SetResult();
			(await replacementWaiter.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(Result(2));
		}
		finally
		{
			releaseFirst.TrySetResult();
			releaseSecond.TrySetResult();
		}
	}

	[Fact]
	public async Task Cancel_WhenRetiredAnalysisObservesCancellation_ShouldContinueWithReplacement()
	{
		var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var calls = 0;
		using var coordinator = new UciPositionAnalysisCoordinator(
			async (_, token) =>
			{
				if (Interlocked.Increment(ref calls) == 1)
				{
					firstStarted.SetResult();
					try
					{
						await Task.Delay(Timeout.InfiniteTimeSpan, token);
					}
					catch (OperationCanceledException)
					{
						cancellationObserved.SetResult();
						throw;
					}
				}

				return Result(2);
			}
		);

		Enqueue(coordinator, "position");
		await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var retiredWaiter = coordinator.GetAnalysisAsync("position");
		var serializer = coordinator.ActiveWorker;

		coordinator.Cancel();
		Enqueue(coordinator, "position");

		coordinator.ActiveWorker.Should().BeSameAs(serializer);
		await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retiredWaiter);
		(await coordinator.GetAnalysisAsync("position").WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(Result(2));
	}

	[Fact]
	public async Task Enqueue_WhenCurrentAnalysisFaults_ShouldContinueWithNextQueuedPosition()
	{
		var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var coordinator = new UciPositionAnalysisCoordinator(
			async (workItem, _) =>
			{
				if (workItem.PositionKey == "first")
				{
					await releaseFirst.Task;
					throw new InvalidOperationException("analysis failed");
				}

				return Result(2);
			}
		);

		Enqueue(coordinator, "first");
		Enqueue(coordinator, "second");
		var first = coordinator.GetAnalysisAsync("first");
		var second = coordinator.GetAnalysisAsync("second");
		releaseFirst.SetResult();

		await Assert.ThrowsAsync<InvalidOperationException>(() => first);
		(await second.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(Result(2));
	}

	[Fact]
	public async Task Enqueue_WhenFailedPositionIsQueuedAgain_ShouldRetry()
	{
		var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var calls = 0;
		using var coordinator = new UciPositionAnalysisCoordinator(
			async (_, _) =>
			{
				if (Interlocked.Increment(ref calls) == 1)
				{
					await releaseFirst.Task;
					throw new InvalidOperationException("analysis failed");
				}

				return Result(2);
			}
		);

		Enqueue(coordinator, "position");
		var failed = coordinator.GetAnalysisAsync("position");
		releaseFirst.SetResult();
		await Assert.ThrowsAsync<InvalidOperationException>(() => failed);

		Enqueue(coordinator, "position");

		(await coordinator.GetAnalysisAsync("position").WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(Result(2));
		calls.Should().Be(2);
	}

	[Fact]
	public async Task CancelPendingAndRetainCompleted_WhenKeysAreRetained_ShouldKeepOnlyRetainedAnalyses()
	{
		using var coordinator = new UciPositionAnalysisCoordinator(
			(workItem, _) => Task.FromResult(Result(workItem.PositionKey == "retained" ? 1 : 2))
		);
		Enqueue(coordinator, "retained");
		Enqueue(coordinator, "discarded");
		await coordinator.GetAnalysisAsync("retained").WaitAsync(TimeSpan.FromSeconds(5));
		await coordinator.GetAnalysisAsync("discarded").WaitAsync(TimeSpan.FromSeconds(5));

		coordinator.CancelPendingAndRetainCompleted(new HashSet<string>(StringComparer.Ordinal) { "retained" });

		coordinator.TryGetAnalysis("retained", out var retained).Should().BeTrue();
		retained.Should().Be(Result(1));
		coordinator.TryGetAnalysis("discarded", out _).Should().BeFalse();
	}

	[Fact]
	public async Task Cancel_WhenAnalysesAreCached_ShouldClearCompletedAnalyses()
	{
		using var coordinator = new UciPositionAnalysisCoordinator((_, _) => Task.FromResult(Result(1)));
		Enqueue(coordinator, "position");
		await coordinator.GetAnalysisAsync("position").WaitAsync(TimeSpan.FromSeconds(5));

		coordinator.Cancel();

		coordinator.TryGetAnalysis("position", out _).Should().BeFalse();
	}

	[Fact]
	public async Task Dispose_WhenCoordinatorIsReused_ShouldRejectNewAnalysis()
	{
		var calls = 0;
		var coordinator = new UciPositionAnalysisCoordinator(
			(_, _) => Task.FromResult(Result(Interlocked.Increment(ref calls)))
		);
		try
		{
			Enqueue(coordinator, "first");
			await coordinator.GetAnalysisAsync("first").WaitAsync(TimeSpan.FromSeconds(5));
			coordinator.Dispose();

			Action enqueue = () => Enqueue(coordinator, "second");
			enqueue.Should().Throw<ObjectDisposedException>();
			calls.Should().Be(1);
		}
		finally
		{
			coordinator.Dispose();
		}
	}

	[Fact]
	public async Task Enqueue_WhenWorkArrivesAsWorkerRetires_ShouldStartReplacementWorker()
	{
		using var workerRetiring = new ManualResetEventSlim();
		using var releaseRetiringWorker = new ManualResetEventSlim();
		var retirementCalls = 0;
		using var coordinator = new UciPositionAnalysisCoordinator(
			(_, _) => Task.FromResult(Result(1)),
			() =>
			{
				if (Interlocked.Increment(ref retirementCalls) != 1)
					return;

				workerRetiring.Set();
				releaseRetiringWorker.Wait();
			}
		);

		Enqueue(coordinator, "first");
		await coordinator.GetAnalysisAsync("first").WaitAsync(TimeSpan.FromSeconds(5));

		try
		{
			workerRetiring.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
			Enqueue(coordinator, "second");

			(await coordinator.GetAnalysisAsync("second").WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(Result(1));
		}
		finally
		{
			releaseRetiringWorker.Set();
		}
	}

	[Fact]
	public async Task Cancel_WhenRetiredAnalysisSucceedsAfterSameKeyReplacement_ShouldSerializeAndIgnoreStaleSuccess()
	{
		var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var calls = 0;
		using var coordinator = new UciPositionAnalysisCoordinator(
			async (_, _) =>
			{
				if (Interlocked.Increment(ref calls) == 1)
				{
					firstStarted.SetResult();
					await releaseFirst.Task;
					return Result(1);
				}

				secondStarted.SetResult();
				await releaseSecond.Task;
				return Result(2);
			}
		);

		try
		{
			coordinator.Enqueue("position", [], 'w', 'w', []);
			await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			var retiredWorker = coordinator.ActiveWorker;
			var retiredWaiter = coordinator.GetAnalysisAsync("position");

			coordinator.Cancel();
			coordinator.Enqueue("position", [], 'w', 'w', []);
			coordinator.ActiveWorker.Should().BeSameAs(retiredWorker);

			var prematureReplacement = await Task.WhenAny(secondStarted.Task, Task.Delay(TimeSpan.FromMilliseconds(250)));
			prematureReplacement.Should().NotBeSameAs(secondStarted.Task);

			releaseFirst.SetResult();
			await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			coordinator.TryGetAnalysis("position", out _).Should().BeFalse();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retiredWaiter);

			releaseSecond.SetResult();
			var replacement = await coordinator.GetAnalysisAsync("position").WaitAsync(TimeSpan.FromSeconds(5));
			replacement.Should().Be(Result(2));
			await retiredWorker!.WaitAsync(TimeSpan.FromSeconds(5));
		}
		finally
		{
			releaseFirst.TrySetResult();
			releaseSecond.TrySetResult();
		}
	}

	[Fact]
	public async Task Enqueue_WhenMultiplePositionsAreQueued_ShouldAnalyzeInFifoOrder()
	{
		var analyzedKeys = new List<string>();
		using var coordinator = new UciPositionAnalysisCoordinator(
			(workItem, _) =>
			{
				analyzedKeys.Add(workItem.PositionKey);
				return Task.FromResult(Result(analyzedKeys.Count));
			}
		);

		coordinator.Enqueue("first", [], 'w', 'w', []);
		coordinator.Enqueue("second", ["e2e4"], 'b', 'w', []);

		await coordinator.GetAnalysisAsync("first").WaitAsync(TimeSpan.FromSeconds(5));
		await coordinator.GetAnalysisAsync("second").WaitAsync(TimeSpan.FromSeconds(5));

		analyzedKeys.Should().Equal("first", "second");
	}

	private static PositionAnalysisResult Result(int centipawns) =>
		new(PositionAdvantage.FromScore(new(centipawns, null)), ImmutableArray<MoveEvaluation>.Empty);

	private static void Enqueue(UciPositionAnalysisCoordinator coordinator, string positionKey) =>
		coordinator.Enqueue(positionKey, [], 'w', 'w', []);
}

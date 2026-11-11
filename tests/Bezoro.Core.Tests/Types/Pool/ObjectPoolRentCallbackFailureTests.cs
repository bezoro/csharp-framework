using System;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.Core.Abstractions;
using Bezoro.Core.Types;
using Bezoro.Core.Types.Pool;
using JetBrains.Annotations;
using Xunit;

namespace Bezoro.Core.Tests.Types.Pool;

[TestSubject(typeof(ObjectPool<>))]
public class ObjectPoolRentCallbackFailureTests
{
	[Theory]
	[InlineData(0, 0, false)]
	[InlineData(1, 0, false)]
	[InlineData(2, 1, false)]
	[InlineData(0, 2, false)]
	[InlineData(0, 3, false)]
	[InlineData(0, 0, true)]
	[InlineData(1, 1, true)]
	public async Task CreatedOrAvailableItem_WhenOnRentThrows_ShouldReleaseCapacityAndPreserveException(
		int source, int rentMethod, bool discardThrows)
	{
		var failure = new InvalidOperationException("rent failed");
		var failedItem = new CallbackItem();
		var policy = new CallbackPolicy(failedItem, discardThrows);
		using var pool = new ObjectPool<CallbackItem>(policy, new PoolOptions(
			InitialCapacity: source == 1 ? 1 : 0, MaxCapacity: 1, TrackStatistics: true));
		if (source == 2)
			Assert.True(pool.Return(pool.Rent()));
		failedItem.RentCallback = () => throw failure;
		var before = pool.Statistics;
		var resetsBefore = policy.ResetCount;
		var returnsBefore = failedItem.ReturnCount;

		var actual = await Record.ExceptionAsync(async () =>
		{
			switch (rentMethod)
			{
				case 0: pool.Rent(); break;
				case 1: pool.TryRent(out _); break;
				case 2: await pool.RentAsync(); break;
				case 3: await pool.RentAsync(TimeSpan.FromSeconds(10)); break;
			}
		});

		Assert.Same(failure, actual);
		Assert.Equal(0, pool.TotalCount);
		Assert.Equal(0, pool.AvailableCount);
		Assert.Equal(1, policy.DiscardCount);
		Assert.Equal(discardThrows ? 0 : 1, failedItem.DisposeCount);
		Assert.Equal(resetsBefore, policy.ResetCount);
		Assert.Equal(returnsBefore, failedItem.ReturnCount);
		Assert.Equal(before.TotalRented, pool.Statistics.TotalRented);
		Assert.Equal(before.TotalReturned, pool.Statistics.TotalReturned);
		Assert.Equal(1, pool.Statistics.TotalDiscarded);

		var replacement = pool.Rent();
		Assert.NotSame(failedItem, replacement);
		Assert.Equal(2, policy.CreateCount);
		Assert.Equal(1, pool.TotalCount);
		Assert.Equal(before.TotalRented + 1, pool.Statistics.TotalRented);
		Assert.True(pool.Return(replacement));
		Assert.Equal(before.TotalReturned + 1, pool.Statistics.TotalReturned);
		if (discardThrows) Assert.Throws<ApplicationException>(() => pool.Dispose());
		else pool.Dispose();
		Assert.Equal(1, replacement.DiscardCount);
		Assert.Equal(discardThrows ? 0 : 1, replacement.DisposeCount);
		Assert.Equal(0, pool.TotalCount);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task FailedRent_WhenAsyncRenterIsWaiting_ShouldWakeItAfterDiscard(bool discardThrows)
	{
		var failure = new InvalidOperationException("rent failed");
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var release = new ManualResetEventSlim();
		using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var failedItem = new CallbackItem { RentCallback = () =>
		{
			entered.SetResult();
			release.Wait(watchdog.Token);
			throw failure;
		} };
		var policy = new CallbackPolicy(failedItem, discardThrows);
		using var pool = new ObjectPool<CallbackItem>(policy, new PoolOptions(
			MaxCapacity: 1, EnableAsyncWait: true, TrackStatistics: true));
		var failingRent = Task.Run(() => Record.Exception(() => pool.Rent()));
		try
		{
			await entered.Task.WaitAsync(watchdog.Token);
			var waitingRent = pool.RentAsync(watchdog.Token).AsTask();
			Assert.False(waitingRent.IsCompleted);
			Assert.Equal(1, pool.Statistics.TotalAsyncWaits);
			release.Set();

			Assert.Same(failure, await failingRent.WaitAsync(watchdog.Token));
			var replacement = await waitingRent.WaitAsync(watchdog.Token);
			Assert.NotSame(failedItem, replacement);
			Assert.Equal(1, policy.DiscardCount);
			Assert.Equal(1, pool.TotalCount);
			Assert.Equal(1, pool.Statistics.TotalRented);
			Assert.Equal(1, pool.Statistics.TotalDiscarded);
		}
		finally
		{
			release.Set();
			await failingRent.WaitAsync(TimeSpan.FromSeconds(10));
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task FailedRent_WhenPoolIsDisposedDuringCallback_ShouldPreserveException(bool discardThrows)
	{
		var failure = new InvalidOperationException("rent failed");
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var release = new ManualResetEventSlim();
		using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var failedItem = new CallbackItem { RentCallback = () =>
		{
			entered.SetResult();
			release.Wait(watchdog.Token);
			throw failure;
		} };
		var policy = new CallbackPolicy(failedItem, discardThrows);
		using var pool = new ObjectPool<CallbackItem>(policy, new PoolOptions(
			MaxCapacity: 1, EnableAsyncWait: true, TrackStatistics: true));
		var failingRent = Task.Run(() => Record.Exception(() => pool.Rent()));
		try
		{
			await entered.Task.WaitAsync(watchdog.Token);
			pool.Dispose();
			release.Set();

			Assert.Same(failure, await failingRent.WaitAsync(watchdog.Token));
			Assert.Equal(0, pool.TotalCount);
			Assert.Equal(0, pool.AvailableCount);
			Assert.Equal(1, policy.DiscardCount);
			Assert.Equal(0, failedItem.ReturnCount);
			Assert.Equal(0, policy.ResetCount);
			Assert.Equal(0, pool.Statistics.TotalRented);
			Assert.Equal(1, pool.Statistics.TotalDiscarded);
		}
		finally
		{
			release.Set();
			await failingRent.WaitAsync(TimeSpan.FromSeconds(10));
		}
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task FailedRent_WhenForeignReturnArrivesAtCapacity_ShouldPreserveOwnershipUntilCallbackEnds(
		bool prewarm, bool discardThrows)
	{
		var failure = new InvalidOperationException("rent failed while ownership was retained");
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var release = new ManualResetEventSlim();
		using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var failedItem = new CallbackItem { RentCallback = () =>
		{
			entered.SetResult();
			release.Wait(watchdog.Token);
			throw failure;
		} };
		var foreign = new CallbackItem();
		var policy = new CallbackPolicy(failedItem, discardThrows);
		using var pool = new ObjectPool<CallbackItem>(policy, new PoolOptions(
			InitialCapacity: prewarm ? 1 : 0, MaxCapacity: 1, TrackStatistics: true));
		var failingRent = Task.Run(() => Record.Exception(() => pool.Rent()));
		try
		{
			await entered.Task.WaitAsync(watchdog.Token);
			if (discardThrows) Assert.Throws<ApplicationException>(() => pool.Return(foreign));
			else Assert.False(pool.Return(foreign));
			Assert.Equal(1, pool.TotalCount);
			Assert.Equal(0, pool.AvailableCount);
			Assert.Equal(1, foreign.DiscardCount);
			Assert.Equal(discardThrows ? 0 : 1, foreign.DisposeCount);
			Assert.Equal(0, foreign.ReturnCount);
			release.Set();

			Assert.Same(failure, await failingRent.WaitAsync(watchdog.Token));
			Assert.Equal(1, failedItem.DiscardCount);
			Assert.Equal(discardThrows ? 0 : 1, failedItem.DisposeCount);
			Assert.Equal(0, failedItem.ReturnCount);
			Assert.Equal(2, policy.DiscardCount);
			Assert.Equal(2, pool.Statistics.TotalDiscarded);
			Assert.Equal(0, pool.Statistics.TotalRented);
			Assert.Equal(0, pool.Statistics.TotalReturned);
			Assert.Equal(0, pool.TotalCount);
			Assert.Equal(0, pool.AvailableCount);

			var replacement = pool.Rent();
			Assert.NotSame(foreign, replacement);
			Assert.NotSame(failedItem, replacement);
			Assert.True(pool.Return(replacement));
			Assert.Equal(1, pool.TotalCount);
			Assert.Equal(1, pool.AvailableCount);
			if (discardThrows) Assert.Throws<ApplicationException>(() => pool.Dispose());
			else pool.Dispose();
			Assert.Equal(1, replacement.DiscardCount);
			Assert.Equal(discardThrows ? 0 : 1, replacement.DisposeCount);
			Assert.Equal(3, pool.Statistics.TotalDiscarded);
			Assert.Equal(0, pool.TotalCount);
		}
		finally
		{
			release.Set();
			await failingRent.WaitAsync(TimeSpan.FromSeconds(10));
		}
	}

	private sealed class CallbackItem : IPooledObject, IDisposable
	{
		public Action? RentCallback { get; set; }
		public int ReturnCount { get; private set; }
		public int DisposeCount { get; private set; }
		public int DiscardCount { get; set; }
		public void OnRent() => RentCallback?.Invoke();
		public bool OnReturn() { ReturnCount++; return true; }
		public void Dispose() => DisposeCount++;
	}

	private sealed class CallbackPolicy(CallbackItem firstItem, bool discardThrows) : IPoolPolicy<CallbackItem>
	{
		public int CreateCount { get; private set; }
		public int DiscardCount { get; private set; }
		public int ResetCount { get; private set; }
		public CallbackItem Create() => ++CreateCount == 1 ? firstItem : new CallbackItem();
		public bool Validate(CallbackItem item) => true;
		public bool Reset(CallbackItem item) { ResetCount++; return item.OnReturn(); }
		public void OnDiscard(CallbackItem item)
		{
			DiscardCount++;
			item.DiscardCount++;
			if (discardThrows)
				throw new ApplicationException("discard failed");
			item.Dispose();
		}
	}
}

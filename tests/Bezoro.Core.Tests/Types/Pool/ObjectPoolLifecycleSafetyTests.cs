using System;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.Core.Types;
using Bezoro.Core.Types.Pool;
using Xunit;

namespace Bezoro.Core.Tests.Types.Pool;

public class ObjectPoolLifecycleSafetyTests
{
	[Fact]
	public void FailedPrewarm_DisposesPreviouslyCreatedItems()
	{
		var item = new DisposableProbe();
		var created = 0;
		Assert.Throws<InvalidOperationException>(() => new ObjectPool<DisposableProbe>(
			() => ++created == 1 ? item : throw new InvalidOperationException("Factory failed."),
			new() { InitialCapacity = 2 }));
		Assert.Equal(1, item.DisposeCount);
	}

	[Fact]
	public void Clear_WhenDiscardThrows_ReleasesEveryAvailableItem()
	{
		var discarded = 0;
		using var pool = new ObjectPool<object>(new PoolPolicy<object>(() => new(), onDiscard: _ =>
		{
			discarded++;
			throw new InvalidOperationException("Discard failed.");
		}), new() { InitialCapacity = 3 });

		Assert.Throws<InvalidOperationException>(() => pool.Clear());
		Assert.Equal(3, discarded);
		Assert.Equal(0, pool.TotalCount);
		Assert.Equal(0, pool.AvailableCount);
	}

	[Fact]
	public void ThrowingValidation_DiscardsItemAndReleasesCapacity()
	{
		var discarded = 0;
		using var pool = new ObjectPool<object>(new PoolPolicy<object>(() => new(),
			validate: _ => throw new InvalidOperationException("Validation failed."), onDiscard: _ => discarded++),
			new() { MaxCapacity = 1, ValidateOnRent = true });
		pool.Return(pool.Rent());
		Assert.Throws<InvalidOperationException>(() => pool.TryRent(out _));
		Assert.Equal(1, discarded);
		Assert.Equal(0, pool.TotalCount);
	}

	[Fact]
	public async Task Dispose_CompletesAllPendingAsyncRentOperations()
	{
		var pool = new ObjectPool<object>(() => new(), new() { MaxCapacity = 1, EnableAsyncWait = true });
		pool.Rent();
		Task<object> indefinite = pool.RentAsync().AsTask();
		Task<object?> timed = pool.RentAsync(TimeSpan.FromMinutes(1)).AsTask();

		pool.Dispose();

		await Assert.ThrowsAsync<ObjectDisposedException>(() => indefinite.WaitAsync(TimeSpan.FromSeconds(5)));
		await Assert.ThrowsAsync<ObjectDisposedException>(() => timed.WaitAsync(TimeSpan.FromSeconds(5)));
	}

	[Fact]
	public async Task InfiniteTimeout_WaitsUntilAnItemIsReturned()
	{
		using var pool = new ObjectPool<object>(() => new(), new() { MaxCapacity = 1, EnableAsyncWait = true });
		var original = pool.Rent();
		Task<object?> waiting = pool.RentAsync(Timeout.InfiniteTimeSpan).AsTask();
		Assert.False(waiting.IsCompleted);
		pool.Return(original);
		Assert.Same(original, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
	}

	[Fact]
	public void DuplicateReturn_DoesNotResetAnAvailableItemAgain()
	{
		var resets = 0;
		using var pool = new ObjectPool<object>(new PoolPolicy<object>(() => new(), reset: _ => { resets++; return true; }));
		var item = pool.Rent();
		Assert.True(pool.Return(item));
		Assert.False(pool.Return(item));
		Assert.Equal(1, resets);
		Assert.Same(item, pool.Rent());
	}

	[Fact]
	public async Task ConcurrentDuplicateReturn_CallsResetOnce()
	{
		using var entered = new ManualResetEventSlim();
		using var resume = new ManualResetEventSlim();
		var resets = 0;
		using var pool = new ObjectPool<object>(new PoolPolicy<object>(() => new(), reset: _ =>
		{
			Interlocked.Increment(ref resets);
			entered.Set();
			if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
			return true;
		}));
		var item = pool.Rent();
		var first = Task.Run(() => pool.Return(item));
		try
		{
			Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
			Assert.False(pool.Return(item));
		}
		finally { resume.Set(); }
		Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.Equal(1, resets);
		Assert.Equal(1, pool.AvailableCount);
	}

	[Fact]
	public async Task RejectedReturn_ReleasesCapacityAndWakesExistingWaiter()
	{
		using var pool = new ObjectPool<object>(new PoolPolicy<object>(() => new(), reset: _ => false),
			new() { MaxCapacity = 1, EnableAsyncWait = true });
		var original = pool.Rent();
		var waiting = pool.RentAsync().AsTask();
		Assert.False(pool.Return(original));
		Assert.NotSame(original, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
	}

	[Fact]
	public void ThrowingReset_DiscardsOwnedItemAndReleasesCapacity()
	{
		var discarded = 0;
		using var pool = new ObjectPool<object>(new PoolPolicy<object>(() => new(),
			reset: _ => throw new InvalidOperationException("Reset failed."), onDiscard: _ => discarded++),
			new() { MaxCapacity = 1 });
		var item = pool.Rent();
		Assert.Throws<InvalidOperationException>(() => pool.Return(item));
		Assert.Equal(1, discarded);
		Assert.Equal(0, pool.TotalCount);
		Assert.NotSame(item, pool.Rent());
	}

	private sealed class DisposableProbe : IDisposable
	{
		public int DisposeCount { get; private set; }
		public void Dispose() => DisposeCount++;
	}
}

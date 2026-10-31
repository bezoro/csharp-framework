using System;
using System.Threading.Tasks;
using System.Threading;
using Bezoro.Core.Types;
using Bezoro.Core.Types.Pool;
using Xunit;

namespace Bezoro.Core.Tests.Types.Pool;

public class ObjectPoolAllocationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void WarmRentReturnDoesNotAllocatePerOperation(bool enableAsyncWait)
	{
		using var pool = new ObjectPool<object>(() => new object(), new PoolOptions(
			InitialCapacity: 1, MaxCapacity: 1, EnableAsyncWait: enableAsyncWait));
		for (int i = 0; i < 100; i++) pool.Return(pool.Rent());
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 1000; i++) pool.Return(pool.Rent());
		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
	}

	[Fact]
	public async Task TimedRentOfAnAvailableItemDoesNotAllocateAStopwatch()
	{
		using var pool = new ObjectPool<object>(() => new object(), new PoolOptions(InitialCapacity: 1, MaxCapacity: 1));
		for (int i = 0; i < 100; i++) pool.Return((await pool.RentAsync(TimeSpan.Zero))!);
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 1000; i++) pool.Return((await pool.RentAsync(TimeSpan.Zero))!);
		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
	}

	[Fact]
	public async Task UnsupportedTimeoutIsRejectedBeforeTakingAnAvailableItem()
	{
		using var pool = new ObjectPool<object>(() => new object(), new PoolOptions(InitialCapacity: 1, MaxCapacity: 1));
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await pool.RentAsync(TimeSpan.MaxValue));
		Assert.Equal(1, pool.AvailableCount);
	}

	[Fact]
	public void PoolOptionsHaveConsistentDefaultsAndZeroCapacityIsExplicit()
	{
		Assert.Equal(PoolOptions.Default, new PoolOptions());
		using var pool = new ObjectPool<object>(() => new object(), new PoolOptions(MaxCapacity: 0));
		Assert.Throws<PoolExhaustedException>(() => pool.Rent());
		Assert.False(pool.TryRent(out _));
		using var allZero = new ObjectPool<object>(() => new object(), new PoolOptions(0, 0, false, false, false));
		Assert.Throws<PoolExhaustedException>(() => allZero.Rent());
	}

	[Theory]
	[InlineData(-1, 1)]
	[InlineData(0, -2)]
	[InlineData(2, 1)]
	public void InvalidCapacityConfigurationFailsBeforeCallingTheFactory(int initial, int maximum)
	{
		int calls = 0;
		Assert.ThrowsAny<ArgumentException>(() => new ObjectPool<object>(() => { calls++; return new object(); },
			new PoolOptions(InitialCapacity: initial, MaxCapacity: maximum)));
		Assert.Equal(0, calls);
	}

	[Fact]
	public async Task AFailedCreationWakesRentersWaitingForItsReservedCapacity()
	{
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		int creations = 0;
		using var pool = new ObjectPool<object>(() =>
		{
			if (Interlocked.Increment(ref creations) == 1)
			{
				entered.Set();
				if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
				throw new InvalidOperationException("Creation failed.");
			}
			return new object();
		}, new PoolOptions(MaxCapacity: 1));
		var first = Task.Run(() => pool.Rent());
		try
		{
			Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
			var waiting = pool.RentAsync().AsTask();
			Assert.False(waiting.IsCompleted);
			release.Set();
			await Assert.ThrowsAsync<InvalidOperationException>(async () => await first);
			var rented = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.NotNull(rented);
			pool.Return(rented);
		}
		finally { release.Set(); }
	}

	[Fact]
	public void AThrowingDiscardCallbackStillDisposesItsValue()
	{
		var item = new DisposableItem();
		var policy = new PoolPolicy<DisposableItem>(() => item, onDiscard: _ => throw new InvalidOperationException());
		Assert.Throws<InvalidOperationException>(() => policy.OnDiscard(item));
		Assert.Equal(1, item.Disposals);
	}

	private sealed class DisposableItem : IDisposable
	{
		internal int Disposals;
		public void Dispose() => Disposals++;
	}
}

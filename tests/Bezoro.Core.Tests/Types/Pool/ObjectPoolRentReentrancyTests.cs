using System;
using Bezoro.Core.Abstractions;
using Bezoro.Core.Types;
using Bezoro.Core.Types.Pool;
using Xunit;

namespace Bezoro.Core.Tests.Types.Pool;

public class ObjectPoolRentReentrancyTests
{
	[Fact]
	public void ValidationCannotReturnItsInProgressRental()
	{
		ObjectPool<object>? pool = null;
		bool? returned = null;
		var policy = new PoolPolicy<object>(() => new object(), validate: item =>
		{
			returned = pool!.Return(item);
			return true;
		});
		using var owner = pool = new ObjectPool<object>(policy, new PoolOptions(InitialCapacity: 1, MaxCapacity: 1));
		var first = pool.Rent();
		Assert.False(returned);
		Assert.False(pool.TryRent(out _));
		Assert.Throws<PoolExhaustedException>(() => pool.Rent());
		Assert.True(pool.Return(first));
	}

	[Fact]
	public void OnRentCannotPublishTheSameObjectForAnotherConsumer()
	{
		ObjectPool<CallbackItem>? pool = null;
		bool? returned = null;
		using var owner = pool = new ObjectPool<CallbackItem>(() => new CallbackItem
		{
			Rented = item => returned = pool!.Return(item)
		}, new PoolOptions(MaxCapacity: 1));
		var first = pool.Rent();
		Assert.False(returned);
		Assert.False(pool.TryRent(out _));
		Assert.Throws<PoolExhaustedException>(() => pool.Rent());
		Assert.True(pool.Return(first));
	}

	[Fact]
	public void AThrowingOnRentAfterARejectedReturnReleasesOwnershipOnce()
	{
		ObjectPool<CallbackItem>? pool = null;
		var item = new CallbackItem
		{
			Rented = value =>
			{
				Assert.False(pool!.Return(value));
				throw new InvalidOperationException("Rent callback failed.");
			}
		};
		using var owner = pool = new ObjectPool<CallbackItem>(() => item, new PoolOptions(MaxCapacity: 1));
		Assert.Throws<InvalidOperationException>(() => pool.Rent());
		Assert.Equal(0, pool.TotalCount);
		Assert.Equal(0, pool.AvailableCount);
		Assert.Equal(1, item.Disposals);
	}

	private sealed class CallbackItem : IPooledObject, IDisposable
	{
		internal Action<CallbackItem>? Rented;
		internal int Disposals;
		public bool OnReturn() => true;
		public void OnRent() => Rented?.Invoke(this);
		public void Dispose() => Disposals++;
	}
}

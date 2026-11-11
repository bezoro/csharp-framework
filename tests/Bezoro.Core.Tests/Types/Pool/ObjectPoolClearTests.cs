using System;
using System.Collections.Generic;
using Bezoro.Core.Types;
using Bezoro.Core.Types.Pool;
using FluentAssertions;
using JetBrains.Annotations;
using Xunit;

namespace Bezoro.Core.Tests.Types.Pool;

[TestSubject(typeof(ObjectPool<>))]
public class ObjectPoolClearTests
{
	[Fact]
	public void Clear_WhenCalled_ShouldRemoveAllAvailableItems()
	{
		var pool = new ObjectPool<object>(
			() => new(),
			new() { InitialCapacity = 5 }
		);

		pool.Clear();

		pool.AvailableCount.Should().Be(0);
		pool.TotalCount.Should().Be(0);
	}

	[Fact]
	public void Clear_WhenCalled_ShouldInvokePolicyAndDisposeItemsExactlyOnce()
	{
		var items = new List<DisposableObject>();
		var discardCount = 0;
		var policy = new PoolPolicy<DisposableObject>(
			() =>
			{
				var item = new DisposableObject();
				items.Add(item);
				return item;
			},
			onDiscard: _ => discardCount++
		);
		var pool = new ObjectPool<DisposableObject>(
			policy,
			new() { InitialCapacity = 3, MaxCapacity = -1 }
		);

		pool.Clear();

		items.Should().HaveCount(3).And.OnlyHaveUniqueItems();
		items.Should().OnlyContain(x => x.IsDisposed);
		items.Should().OnlyContain(x => x.DisposeCount == 1);
		pool.TotalCount.Should().Be(0);
		discardCount.Should().Be(3);
	}

	[Fact]
	public void Clear_WhenCalled_ShouldDiscardEachAvailableItemAndReleaseCapacity()
	{
		var policy = new TrackingPoolPolicy();
		var pool = new ObjectPool<object>(policy, new() { InitialCapacity = 3, TrackStatistics = true });

		pool.Clear();

		pool.AvailableCount.Should().Be(0);
		pool.TotalCount.Should().Be(0);
		policy.DiscardCount.Should().Be(3);
	}

}

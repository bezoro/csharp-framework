using System;
using System.Collections.Generic;
using Bezoro.Core.Types;
using Bezoro.Core.Types.Pool;
using FluentAssertions;
using JetBrains.Annotations;
using Xunit;

namespace Bezoro.Core.Tests.Types.Pool;

[TestSubject(typeof(ObjectPool<>))]
public class ObjectPoolDisposeTests
{
	[Fact]
	public void Dispose_WhenCalled_ShouldInvokePolicyAndDisposeAllAvailableItems()
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

		pool.Dispose();

		items.Should().HaveCount(3).And.OnlyHaveUniqueItems();
		items.Should().OnlyContain(x => x.IsDisposed);
		items.Should().OnlyContain(x => x.DisposeCount == 1);
		pool.TotalCount.Should().Be(0);
		discardCount.Should().Be(3);
	}

	[Fact]
	public void Dispose_WhenCalled_ShouldPreventFurtherRent()
	{
		var pool = new ObjectPool<object>(() => new());
		pool.Dispose();

		var act = () => pool.Rent();

		act.Should().Throw<ObjectDisposedException>();
	}

	[Fact]
	public void WhenCalledMultipleTimes_WhenCalled_ShouldBeIdempotent()
	{
		var pool = new ObjectPool<object>(() => new());

		pool.Dispose();
		var act = () => pool.Dispose();

		act.Should().NotThrow();
	}
}

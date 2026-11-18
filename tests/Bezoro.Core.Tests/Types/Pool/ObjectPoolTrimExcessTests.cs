using Bezoro.Core.Types;
using Bezoro.Core.Types.Pool;
using FluentAssertions;
using JetBrains.Annotations;
using Xunit;

namespace Bezoro.Core.Tests.Types.Pool;

[TestSubject(typeof(ObjectPool<>))]
public class ObjectPoolTrimExcessTests
{
	[Fact]
	public void ObjectPoolTrimExcess_WhenCalled_ShouldShouldRemoveExcessItems()
	{
		var pool = new ObjectPool<object>(
			() => new(),
			new() { InitialCapacity = 10 }
		);

		int removed = pool.TrimExcess(Percent.Ninety);

		removed.Should().BeGreaterThan(0);
		pool.AvailableCount.Should().BeLessThan(10);
	}

	[Fact]
	public void WhenNoExcess_WhenCalled_ShouldReturnZero()
	{
		var pool = new ObjectPool<object>(() => new());

		int removed = pool.TrimExcess();

		removed.Should().Be(0);
	}

	[Fact]
	public void TrimExcess_WhenDiscardingAvailableItems_ShouldReleaseEachCapacitySlotOnce()
	{
		var policy = new TrackingPoolPolicy();
		var pool = new ObjectPool<object>(policy, new() { InitialCapacity = 3, TrackStatistics = true });

		pool.Rent();
		pool.TrimExcess(default).Should().Be(0);
		pool.TrimExcess(Percent.Half).Should().Be(1);

		pool.AvailableCount.Should().Be(1);
		pool.TotalCount.Should().Be(2);
		pool.Statistics.TotalDiscarded.Should().Be(1);
		policy.DiscardCount.Should().Be(1);
	}
}

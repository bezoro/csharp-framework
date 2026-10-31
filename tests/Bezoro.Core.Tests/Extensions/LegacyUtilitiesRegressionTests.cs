using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.Core.Extensions;
using Bezoro.Core.Helpers;
using Xunit;

namespace Bezoro.Core.Tests.Extensions;

public class LegacyUtilitiesRegressionTests
{
	[Fact]
	public void ArrayContainsSkipsNullSlots()
	{
		string[] values = [null!, "found"];
		Assert.True(values.Contains("found", out int index));
		Assert.Equal(1, index);
	}

	[Fact]
	public void GenericHasAnyUsesTheReadOnlyCollectionCount()
	{
		Assert.True(new CountOnlyCollection().HasAny());
	}

	[Fact]
	public void MapSupportsSmallAndVeryWideFiniteRanges()
	{
		Assert.Equal(0.5f, 5e-9f.Map(0, 1e-8f, 0, 1));
		Assert.Equal(0.5f, 0f.Map(-float.MaxValue, float.MaxValue, 0, 1));
	}

	[Fact]
	public void RelativeComparisonsDoNotEquateFiniteValuesWithInfinity()
	{
		Assert.False(FloatComparer.AreEqualRelative(1, float.PositiveInfinity));
		Assert.False(DoubleComparer.AreEqualRelative(1, double.PositiveInfinity));
	}

	[Theory]
	[InlineData(1.5, false)]
	[InlineData(2, true)]
	public void RelativeComparisonsPreserveDistancesBetweenOppositeFiniteExtremes(double tolerance, bool expected)
	{
		Assert.Equal(expected, FloatComparer.AreEqualRelative(float.MaxValue, -float.MaxValue, 0, (float)tolerance));
		Assert.Equal(expected, FloatComparer.AreEqualRobust(float.MaxValue, -float.MaxValue, 0, (float)tolerance));
		Assert.Equal(expected, DoubleComparer.AreEqualRelative(double.MaxValue, -double.MaxValue, 0, tolerance));
		Assert.Equal(expected, DoubleComparer.AreEqualRobust(double.MaxValue, -double.MaxValue, 0, tolerance));
	}

	[Fact]
	public void ReshapeRejectsUnrepresentableAreaBeforeAllocation()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => ArrayConverter.Reshape(Array.Empty<int>(), 65536, 65536));
	}

	[Fact]
	public void CompletedArrayCallbacksDoNotForceASchedulerYield()
	{
		int[,] values = { { 1, 2 } };
		int count = 0;
		var processing = values.ProcessAsync((_, _, _, _) => { count++; return Task.CompletedTask; });
		Assert.True(processing.IsCompletedSuccessfully);
		Assert.Equal(2, count);
	}

	[Fact]
	public async Task ArrayProcessingPropagatesUnrelatedCancellation()
	{
		int[,] values = { { 1 } };
		await Assert.ThrowsAsync<OperationCanceledException>(() => values.ProcessAsync((_, _, _, _) =>
			throw new OperationCanceledException()));
	}

	[Fact]
	public async Task ArrayProcessingObservesCancellationFromTheFinalCallback()
	{
		int[,] values = { { 1 } };
		using var cancellation = new CancellationTokenSource();
		await Assert.ThrowsAsync<OperationCanceledException>(() => values.ProcessAsync((_, _, _, _) =>
		{
			cancellation.Cancel();
			return Task.CompletedTask;
		}, cancellation.Token));
	}

	[Fact]
	public async Task EmptyArrayProcessingStillValidatesItsCallback()
	{
		await Assert.ThrowsAsync<ArgumentNullException>(() => new int[0, 0].ProcessAsync(null!));
	}

	[Fact]
	public void RepeatRejectsAnUnrepresentableOutputLength()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => "abc".Repeat(uint.MaxValue));
	}

	[Fact]
	public void StringPredicatesDoNotAllocateTrimmedCopies()
	{
		string value = " text ";
		for (int i = 0; i < 100; i++) value.IsEmpty();
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 100; i++) value.IsEmpty();
		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
	}

	[Fact]
	public void ArrayEqualityDoesNotBoxValueElements()
	{
		int[] first = [1000, 2000];
		int[] second = [1000, 2000];
		for (int i = 0; i < 100; i++) ArrayHelper.CompareArrays(first, second);
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 100; i++)
		{
			ArrayHelper.CompareArrays(first, second);
			first.AreEqual(second);
		}
		Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
	}

	private sealed class CountOnlyCollection : IReadOnlyCollection<int>
	{
		public int Count => 1;
		public IEnumerator<int> GetEnumerator() => throw new InvalidOperationException("Count must avoid enumeration.");
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.Events.Abstractions;
using Bezoro.Events.Tests.Services.Fixtures;
using Xunit;

namespace Bezoro.Events.Tests.Services.EventBus;

public sealed class EventBusAdmissionTests
{
	[Fact]
	public void ConcurrentProducers_CannotExceedConfiguredCapacity()
	{
		using IEventBus bus = new Events.Services.EventBus(maximumQueuedEvents: 31);
		int accepted = 0;
		Parallel.For(0, 10_000, value =>
		{
			if (bus.TryEnqueue(new TestEventA(value))) Interlocked.Increment(ref accepted);
		});
		Assert.Equal(31, bus.MaximumQueuedEvents);
		Assert.Equal(31, accepted);
		Assert.Equal(31, bus.QueuedCount);
		Assert.Equal(1, bus.FlushQueued(1));
		Assert.True(bus.TryEnqueue(new TestEventA(10_000)));
		Assert.Equal(31, bus.QueuedCount);
	}

	[Fact]
	public void FullAdmission_RejectsWithoutAllocatingAnEnvelope()
	{
		using var bus = new Events.Services.EventBus(maximumQueuedEvents: 1);
		bus.Enqueue(new TestEventA(1));
		for (int i = 0; i < 100; i++) bus.TryEnqueue(new TestEventA(i));
		int accepted = 0;
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 1000; i++)
			if (bus.TryEnqueue(new TestEventA(i))) accepted++;
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.Equal(0, accepted);
		Assert.Equal(0, allocated);
		Assert.Throws<InvalidOperationException>(() => bus.Enqueue(new TestEventA(2)));
		Assert.Equal(1, bus.QueuedCount);
	}

	[Fact]
	public void ZeroCapacity_DisablesQueueingWithoutDisablingImmediatePublication()
	{
		using var bus = new Events.Services.EventBus(maximumQueuedEvents: 0);
		int received = 0;
		bus.Subscribe<TestEventA>(_ => received++);
		Assert.False(bus.TryEnqueue(new TestEventA(1)));
		Assert.Throws<InvalidOperationException>(() => bus.Enqueue(new TestEventA(1)));
		bus.Publish(new TestEventA(1));
		Assert.Equal(1, received);
		Assert.Equal(0, bus.QueuedCount);
	}

	[Fact]
	public void DisposedQueue_ThrowsBeforeCapacityRejection()
	{
		var bus = new Events.Services.EventBus(maximumQueuedEvents: 0);
		bus.Dispose();
		Assert.Throws<ObjectDisposedException>(() => bus.TryEnqueue(new TestEventA(1)));
		Assert.Throws<ObjectDisposedException>(() => bus.Enqueue(new TestEventA(1)));
		Assert.Equal(0, bus.QueuedCount);
	}

	[Theory]
	[InlineData(-2)]
	[InlineData(int.MinValue)]
	public void InvalidCapacity_RejectsAtConstruction(int capacity) =>
		Assert.Throws<ArgumentOutOfRangeException>(() => new Events.Services.EventBus(capacity));

	[Fact]
	public void DefaultConstructor_PreservesExplicitUnboundedPolicy()
	{
		using var bus = new Events.Services.EventBus();
		Assert.Equal(-1, bus.MaximumQueuedEvents);
		Assert.True(bus.TryEnqueue(new TestEventA(1)));
	}

	[Fact]
	public void CallbackReposting_UsesOnlyTheInitialCountInADefaultPass()
	{
		using var bus = new Events.Services.EventBus(maximumQueuedEvents: 1);
		int received = 0;
		bus.Subscribe<TestEventA>(context =>
		{
			received++;
			if (received < 3) Assert.True(bus.TryEnqueue(new TestEventA(context.Data.Value + 1)));
		});
		bus.Enqueue(new TestEventA(0));
		Assert.Equal(1, bus.FlushQueued());
		Assert.Equal(1, received);
		Assert.Equal(1, bus.QueuedCount);
		Assert.Equal(1, bus.FlushQueued());
		Assert.Equal(2, received);
		Assert.Equal(1, bus.QueuedCount);
		Assert.Equal(1, bus.FlushQueued());
		Assert.Equal(3, received);
		Assert.Equal(0, bus.QueuedCount);
	}
}

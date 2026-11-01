using System;
using Bezoro.Events.Abstractions;
using Bezoro.Events.Tests.Services.Fixtures;
using Bezoro.Events.Types;
using Xunit;

namespace Bezoro.Events.Tests.Services.UnityEventBuses;

public sealed class UnityEventBusesLifecycleTests
{
	[Fact]
	public void Dispose_ReleasesEveryUniqueBusEvenWhenOneFails()
	{
		var first = new TrackedBus { FailDisposal = true };
		var second = new TrackedBus();
		var buses = new Events.Services.UnityEventBuses(first, second, first);
		var error = Assert.Throws<AggregateException>(buses.Dispose);
		Assert.Single(error.InnerExceptions);
		Assert.Equal(1, first.DisposalCount);
		Assert.Equal(1, second.DisposalCount);
		buses.Dispose();
		Assert.Equal(1, first.DisposalCount);
		Assert.Equal(1, second.DisposalCount);
	}

	[Fact]
	public void BorrowedBuses_RemainUsableAfterAggregateDisposal()
	{
		using var shared = new Events.Services.EventBus();
		var buses = new Events.Services.UnityEventBuses(shared, shared, shared, ownsBuses: false);
		buses.Dispose();
		Assert.Throws<ObjectDisposedException>(() => buses.FlushUpdate(0));
		shared.Enqueue(new TestEventA(1));
		Assert.Equal(1, shared.FlushQueued());
	}

	[Fact]
	public void Budget_IsAvailableThroughThePhaseContract()
	{
		using IUnityEventBuses buses = new Events.Services.UnityEventBuses();
		buses.Update.Enqueue(new TestEventA(1));
		buses.Update.Enqueue(new TestEventA(2));
		Assert.Equal(1, buses.FlushUpdate(maxEvents: 1));
		Assert.Equal(1, buses.Update.QueuedCount);
	}

	private sealed class TrackedBus : IEventBus
	{
		public int DisposalCount { get; private set; }
		public bool FailDisposal { get; init; }
		public int QueuedCount => 0;
		public int SubscriptionCount => 0;
		public bool Unsubscribe(SubscriptionHandle handle) => false;
		public EventContext<TEvent> Publish<TEvent>(TEvent evt) where TEvent : struct => new(evt);
		public int FlushQueued() => 0;
		public int FlushQueued(int maxEvents) => 0;
		public SubscriptionHandle Subscribe<TEvent>(Action<EventContext<TEvent>> handler, int priority = 0) where TEvent : struct => default;
		public void Enqueue<TEvent>(TEvent evt) where TEvent : struct { }
		public void Dispose()
		{
			DisposalCount++;
			if (FailDisposal) throw new InvalidOperationException("Disposal failure.");
		}
	}
}

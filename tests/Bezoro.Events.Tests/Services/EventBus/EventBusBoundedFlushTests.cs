using System;
using System.Collections.Generic;
using Bezoro.Events.Abstractions;
using Bezoro.Events.Tests.Services.Fixtures;
using FluentAssertions;
using JetBrains.Annotations;
using Xunit;

namespace Bezoro.Events.Tests.Services.EventBus;

[TestSubject(typeof(Events.Services.EventBus))]
public class EventBusBoundedFlushTests
{
	[Fact]
	public void FlushQueued_WithoutBudget_ShouldDrainFiniteCallbackChain()
	{
		using var bus = new Events.Services.EventBus();
		var received = 0;
		bus.Subscribe<TestEventA>(ctx =>
		{
			received++;
			if (ctx.Data.Value < 10)
				bus.Enqueue(new TestEventA(ctx.Data.Value + 1));
		});
		bus.Enqueue(new TestEventA(1));

		bus.FlushQueued().Should().Be(10);
		received.Should().Be(10);
		bus.QueuedCount.Should().Be(0);
	}

	[Fact]
	public void FlushQueued_WithBudget_ShouldLimitCountAndPreserveFifoAcrossCalls()
	{
		using IEventBus bus = new Events.Services.EventBus();
		var received = new List<string>();
		bus.Subscribe<TestEventA>(ctx => received.Add($"A{ctx.Data.Value}"));
		bus.Subscribe<TestEventB>(ctx => received.Add($"B{ctx.Data.Message}"));
		bus.Enqueue(new TestEventA(1));
		bus.Enqueue(new TestEventB("x"));
		bus.Enqueue(new TestEventA(2));
		bus.Enqueue(new TestEventB("y"));

		bus.FlushQueued(2).Should().Be(2);
		received.Should().Equal("A1", "Bx");
		bus.QueuedCount.Should().Be(2);
		bus.FlushQueued(1).Should().Be(1);
		received.Should().Equal("A1", "Bx", "A2");
		bus.QueuedCount.Should().Be(1);
		bus.FlushQueued(2).Should().Be(1);
		received.Should().Equal("A1", "Bx", "A2", "By");
		bus.QueuedCount.Should().Be(0);
	}

	[Fact]
	public void FlushQueued_WithZeroBudget_ShouldLeaveQueueAndHandlersUntouched()
	{
		using var bus = new Events.Services.EventBus();
		var received = new List<int>();
		bus.Subscribe<TestEventA>(ctx => received.Add(ctx.Data.Value));
		bus.Enqueue(new TestEventA(1));
		bus.Enqueue(new TestEventA(2));

		bus.FlushQueued(0).Should().Be(0);
		bus.QueuedCount.Should().Be(2);
		received.Should().BeEmpty();
		bus.FlushQueued(2).Should().Be(2);
		received.Should().Equal(1, 2);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(int.MinValue)]
	public void FlushQueued_WithNegativeBudget_ShouldRejectBeforeDequeue(int budget)
	{
		using var bus = new Events.Services.EventBus();
		var received = new List<int>();
		bus.Subscribe<TestEventA>(ctx => received.Add(ctx.Data.Value));
		bus.Enqueue(new TestEventA(1));
		bus.Enqueue(new TestEventA(2));
		Action flush = () => bus.FlushQueued(budget);

		flush.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be("maxEvents");
		bus.QueuedCount.Should().Be(2);
		received.Should().BeEmpty();
		bus.FlushQueued(2).Should().Be(2);
		received.Should().Equal(1, 2);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(int.MaxValue)]
	public void FlushQueued_WithEmptyQueue_ShouldReturnZero(int budget)
	{
		using var bus = new Events.Services.EventBus();
		bus.FlushQueued(budget).Should().Be(0);
		bus.QueuedCount.Should().Be(0);
	}

	[Theory]
	[InlineData(2)]
	[InlineData(10)]
	[InlineData(int.MaxValue)]
	public void FlushQueued_WithUnhandledEvents_ShouldCountDequeuesUntilEmpty(int budget)
	{
		using var bus = new Events.Services.EventBus();
		bus.Enqueue(new TestEventA(1));
		bus.Enqueue(new TestEventB("x"));

		bus.FlushQueued(budget).Should().Be(2);
		bus.QueuedCount.Should().Be(0);
	}

	[Fact]
	public void FlushQueued_WithCallbackEnqueues_ShouldConsumeRemainingBudget()
	{
		using var bus = new Events.Services.EventBus();
		var received = new List<int>();
		bus.Subscribe<TestEventA>(ctx =>
		{
			received.Add(ctx.Data.Value);
			if (ctx.Data.Value < 3)
				bus.Enqueue(new TestEventA(ctx.Data.Value + 1));
		});
		bus.Enqueue(new TestEventA(1));

		bus.FlushQueued(2).Should().Be(2);
		received.Should().Equal(1, 2);
		bus.QueuedCount.Should().Be(1);
		bus.FlushQueued(2).Should().Be(1);
		received.Should().Equal(1, 2, 3);
		bus.QueuedCount.Should().Be(0);
	}

	[Fact]
	public void FlushQueued_WithContinuousCallbackEnqueues_ShouldStopAtBudgetWithOnePending()
	{
		using var bus = new Events.Services.EventBus();
		var received = new List<int>();
		bus.Subscribe<TestEventA>(ctx =>
		{
			received.Add(ctx.Data.Value);
			bus.Enqueue(new TestEventA(ctx.Data.Value + 1));
		});
		bus.Enqueue(new TestEventA(1));

		bus.FlushQueued(5).Should().Be(5);
		received.Should().Equal(1, 2, 3, 4, 5);
		bus.QueuedCount.Should().Be(1);
		bus.FlushQueued(2).Should().Be(2);
		received.Should().Equal(1, 2, 3, 4, 5, 6, 7);
		bus.QueuedCount.Should().Be(1);
	}

	[Fact]
	public void FlushQueued_WhenHandlerThrows_ShouldCountEventAndContinueDispatch()
	{
		using var bus = new Events.Services.EventBus();
		var received = new List<int>();
		bus.Subscribe<TestEventA>(_ => throw new InvalidOperationException("handler failure"), priority: 1);
		bus.Subscribe<TestEventA>(ctx => received.Add(ctx.Data.Value));
		bus.Enqueue(new TestEventA(1));
		bus.Enqueue(new TestEventA(2));
		bus.Enqueue(new TestEventA(3));

		bus.FlushQueued(2).Should().Be(2);
		received.Should().Equal(1, 2);
		bus.QueuedCount.Should().Be(1);
		bus.FlushQueued(1).Should().Be(1);
		received.Should().Equal(1, 2, 3);
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(int.MinValue)]
	[InlineData(0)]
	[InlineData(1)]
	public void FlushQueued_WhenDisposed_ShouldRejectBeforeBudgetValidation(int budget)
	{
		var bus = new Events.Services.EventBus();
		bus.Dispose();
		Action flush = () => bus.FlushQueued(budget);

		flush.Should().Throw<ObjectDisposedException>();
	}

	[Fact]
	public void FlushQueued_WithNestedFlush_ShouldKeepBudgetsAndCountsIndependent()
	{
		using var bus = new Events.Services.EventBus();
		var received = new List<int>();
		var nestedCount = -1;
		bus.Subscribe<TestEventA>(ctx =>
		{
			received.Add(ctx.Data.Value);
			if (ctx.Data.Value == 1)
				nestedCount = bus.FlushQueued(1);
		});
		for (var value = 1; value <= 4; value++)
			bus.Enqueue(new TestEventA(value));

		bus.FlushQueued(2).Should().Be(2);
		nestedCount.Should().Be(1);
		received.Should().Equal(1, 2, 3);
		bus.QueuedCount.Should().Be(1);
		bus.FlushQueued(1).Should().Be(1);
		received.Should().Equal(1, 2, 3, 4);
	}
}

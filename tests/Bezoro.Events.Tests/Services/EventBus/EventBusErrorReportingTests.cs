using System;
using System.Collections.Generic;
using Bezoro.Events.Tests.Services.Fixtures;
using Xunit;

namespace Bezoro.Events.Tests.Services.EventBus;

public sealed class EventBusErrorReportingTests
{
	[Fact]
	public void HandlerFailure_IsReportedAndDoesNotStopDispatch()
	{
		var observed = new List<Exception>();
		using var bus = new Events.Services.EventBus(reportHandlerError: observed.Add);
		var expected = new InvalidOperationException("Handler failure.");
		bus.Subscribe<TestEventA>(_ => throw expected);
		var calls = 0;
		bus.Subscribe<TestEventA>(_ => calls++);
		bus.Publish(new TestEventA(1));
		bus.Enqueue(new TestEventA(2));
		Assert.Equal(1, bus.FlushQueued(1));
		Assert.Equal(new[] { expected, expected }, observed);
		Assert.Equal(2, calls);
	}

	[Fact]
	public void ReporterFailure_DoesNotStopRemainingHandlers()
	{
		using var bus = new Events.Services.EventBus(reportHandlerError: _ => throw new Exception("Reporter failure."));
		bus.Subscribe<TestEventA>(_ => throw new Exception("Handler failure."));
		var calls = 0;
		bus.Subscribe<TestEventA>(_ => calls++);
		bus.Publish(new TestEventA(1));
		Assert.Equal(1, calls);
	}
}

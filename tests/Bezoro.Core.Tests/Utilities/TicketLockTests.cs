using System;
using System.Reflection;
using Bezoro.Core.Utilities;
using Xunit;

namespace Bezoro.Core.Tests.Utilities;

public class TicketLockTests
{
	[Fact]
	public void TicketCountersWrapTogetherInCheckedBuilds()
	{
		var ordered = new TicketLock();
		typeof(TicketLock).GetField("_nextTicket", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(ordered, long.MaxValue - 1);
		typeof(TicketLock).GetField("_servingTicket", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(ordered, long.MaxValue);
		int calls = 0;
		ordered.ExecuteOrdered(() => calls++);
		ordered.ExecuteOrdered(() => calls++);
		Assert.Equal(2, calls);
	}

	[Fact]
	public void AThrowingActionDoesNotBlockTheNextTicket()
	{
		var ordered = new TicketLock();
		Assert.Throws<InvalidOperationException>(() => ordered.ExecuteOrdered(() => throw new InvalidOperationException()));
		int calls = 0;
		ordered.ExecuteOrdered(() => calls++);
		Assert.Equal(1, calls);
	}
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

public sealed class WorldSchedulerConcurrencyTests
{
	[Fact]
	public void RegistrationDuringAnUpdate_RejectsBeforeCallingTheNewSystem()
	{
		using var world = CreateWorld();
		var extra = new ProbeSystem();
		world.AddSystem(new CallbackSystem(_ => Assert.Throws<InvalidOperationException>(() => world.AddSystem(extra))));
		world.Tick(0.1f);
		Assert.Equal(0, extra.CreateCount);
		Assert.Equal(0, extra.UpdateCount);
	}

	[Fact]
	public async Task OverlappingRegistrations_RejectBeforeMutatingTheSchedule()
	{
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		using var world = CreateWorld();
		var extra = new ProbeSystem();
		var initializing = new CallbackSystem(_ => { }, _ =>
		{
			entered.Set();
			if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
		});
		Task registration = Task.Run(() => world.AddSystem(initializing));
		try
		{
			Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
			Assert.Throws<InvalidOperationException>(() => world.AddSystem(extra));
			Assert.Throws<InvalidOperationException>(() => world.Tick(0.1f));
			Assert.Throws<InvalidOperationException>(world.Dispose);
			Assert.Equal(0, extra.CreateCount);
		}
		finally
		{
			release.Set();
			await registration.WaitAsync(TimeSpan.FromSeconds(10));
		}
		world.AddSystem(extra);
		world.Tick(0.1f);
		Assert.Equal(1, extra.CreateCount);
		Assert.Equal(1, extra.UpdateCount);
	}

	[Fact]
	public void NestedRegistrationOnTheOwningThread_PreservesCompleteInitialization()
	{
		using var world = CreateWorld();
		var nested = new ProbeSystem();
		world.AddSystem(new CallbackSystem(_ => { }, owner => owner.AddSystem(nested)));
		Assert.Equal(1, nested.CreateCount);
		world.Tick(0.1f);
		Assert.Equal(1, nested.UpdateCount);
	}

	private static World CreateWorld() => new(new WorldConfig
	{
		EntityCapacity = 4, QueryResultCapacity = 4, ComponentTypeCapacity = 2, MaxDegreeOfParallelism = 1
	});
	private sealed class ProbeSystem : ISystem
	{
		public int CreateCount { get; private set; }
		public int UpdateCount { get; private set; }
		public void OnCreate(World world) => CreateCount++;
		public void Update(in SystemContext context) => UpdateCount++;
	}
	private sealed class CallbackSystem(Action<SystemContext> update, Action<World>? create = null) : ISystem
	{
		public void OnCreate(World world) => create?.Invoke(world);
		public void Update(in SystemContext context) => update(context);
	}
}

using System;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

public sealed class WorldShutdownFailureTests
{
	[Fact]
	public void Dispose_WhenSystemShutdownThrows_ReleasesResourcesAndClosesWorld()
	{
		var world = CreateWorld();
		var resource = new DisposableResource();
		world.SetResource(resource);
		world.AddSystem(new FailingShutdownSystem());
		world.Spawn(new Counter { Value = 12 });

		Action dispose = world.Dispose;
		dispose.Should().Throw<InvalidOperationException>().WithMessage("Shutdown failed.");
		resource.DisposeCount.Should().Be(1);
		Action spawn = () => world.Spawn();
		spawn.Should().Throw<ObjectDisposedException>();
		world.Dispose();
		resource.DisposeCount.Should().Be(1);
	}

	[Fact]
	public void Dispose_WhenShutdownReentersDispose_RunsDestructionOnce()
	{
		var world = CreateWorld();
		var system = new ReentrantShutdownSystem();
		world.AddSystem(system);
		world.Dispose();
		system.DestroyCount.Should().Be(1);
	}

	[Fact]
	public void AddSystem_WhenCreationFails_DoesNotRunUninitializedSystem()
	{
		using var world = CreateWorld();
		var system = new FailingCreationSystem();
		Action add = () => world.AddSystem(system);
		add.Should().Throw<InvalidOperationException>().WithMessage("Creation failed.");
		world.Tick(0.1f);
		world.Dispose();
		system.UpdateCount.Should().Be(0);
		system.DestroyCount.Should().Be(0);
	}

	[Fact]
	public void OnCreate_CannotDisposeTheWorldBeforeRegistrationCompletes()
	{
		using var world = CreateWorld();
		world.AddSystem(new DisposingCreationSystem());
		world.Spawn(new Counter());
		world.EntityCount.Should().Be(1);
	}

	[Fact]
	public void OnCreate_CannotUpdateAnUninitializedSystem()
	{
		using var world = CreateWorld();
		var system = new LifecycleUpdateSystem { UpdateDuringCreation = true };
		world.AddSystem(system);
		system.UpdateCount.Should().Be(0);
		world.Tick(0.1f);
		system.UpdateCount.Should().Be(1);
	}

	[Fact]
	public void OnDestroy_CannotUpdateSystemsDuringTeardown()
	{
		var world = CreateWorld();
		var system = new LifecycleUpdateSystem { UpdateDuringDestruction = true };
		world.AddSystem(system);
		world.Dispose();
		system.UpdateCount.Should().Be(0);
	}

	private sealed class LifecycleUpdateSystem : ISystem
	{
		public bool UpdateDuringCreation { get; init; }
		public bool UpdateDuringDestruction { get; init; }
		public int UpdateCount { get; private set; }
		public void OnCreate(World world)
		{
			if (!UpdateDuringCreation) return;
			Action tick = () => world.Tick(0.1f);
			tick.Should().Throw<InvalidOperationException>().WithMessage("*registration*");
		}
		public void OnDestroy(World world)
		{
			if (!UpdateDuringDestruction) return;
			Action tick = () => world.Tick(0.1f);
			tick.Should().Throw<InvalidOperationException>().WithMessage("*disposal*");
		}
		public void Update(in SystemContext context) => UpdateCount++;
	}

	private sealed class DisposingCreationSystem : ISystem
	{
		public void OnCreate(World world)
		{
			Action dispose = world.Dispose;
			dispose.Should().Throw<InvalidOperationException>().WithMessage("*system registration*");
		}
		public void Update(in SystemContext context) { }
	}

	private static World CreateWorld() => new(new WorldConfig
	{
		EntityCapacity = 4, ComponentTypeCapacity = 4, QueryResultCapacity = 4,
		CommandCapacity = 4, CommandPayloadCapacityPerType = 4, ChunkCapacity = 4
	});

	private struct Counter { public int Value; }
	private sealed class DisposableResource : IDisposable
	{
		public int DisposeCount { get; private set; }
		public void Dispose() => DisposeCount++;
	}
	private sealed class FailingShutdownSystem : ISystem
	{
		public void Update(in SystemContext context) { }
		public void OnDestroy(World world) => throw new InvalidOperationException("Shutdown failed.");
	}
	private sealed class ReentrantShutdownSystem : ISystem
	{
		public int DestroyCount { get; private set; }
		public void Update(in SystemContext context) { }
		public void OnDestroy(World world) { DestroyCount++; world.Dispose(); }
	}
	private sealed class FailingCreationSystem : ISystem
	{
		public int DestroyCount { get; private set; }
		public int UpdateCount { get; private set; }
		public void OnCreate(World world) => throw new InvalidOperationException("Creation failed.");
		public void Update(in SystemContext context) => UpdateCount++;
		public void OnDestroy(World world) => DestroyCount++;
	}
}

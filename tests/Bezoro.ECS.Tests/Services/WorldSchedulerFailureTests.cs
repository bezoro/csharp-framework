using System;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

public sealed class WorldSchedulerFailureTests
{
	[Fact]
	public void FailingSystemUpdate_DisposesItsCapturedCommandStream()
	{
		using var world = CreateWorld(4);
		var system = new CapturingSystem { ThrowDuringUpdate = true };
		world.AddSystem(system);
		Action tick = () => world.Tick(0.1f);

		tick.Should().Throw<InvalidOperationException>().WithMessage("Update failed.");
		AssertDisposed(system.Captured!);
		world.EntityCount.Should().Be(0);
		system.ThrowDuringUpdate = false;
		world.Tick(0.1f);
		world.EntityCount.Should().Be(2);
	}

	[Fact]
	public void FailingCommandPlayback_DisposesAllStreamsInBatch()
	{
		using var world = CreateWorld(1);
		var first = new CapturingSystem();
		var second = new CapturingSystem();
		world.AddSystem(first);
		world.AddSystem(second);
		Action tick = () => world.Tick(0.1f);

		tick.Should().Throw<InvalidOperationException>();
		AssertDisposed(first.Captured!);
		AssertDisposed(second.Captured!);
	}

	[Theory]
	[InlineData(float.NaN)]
	[InlineData(float.PositiveInfinity)]
	[InlineData(float.NegativeInfinity)]
	[InlineData(-1f)]
	public void InvalidDeltaTime_IsRejectedBeforeUpdatingIntervalAccumulator(float deltaTime)
	{
		using var world = CreateWorld(4);
		var system = new IntervalSystem();
		world.AddSystem(system);
		Action tick = () => world.Tick(deltaTime);
		tick.Should().Throw<ArgumentOutOfRangeException>();
		system.Count.Should().Be(0);
		world.Tick(0.5f);
		system.Count.Should().Be(0);
		world.Tick(0.5f);
		system.Count.Should().Be(1);
	}

	[Theory]
	[InlineData(float.NaN)]
	[InlineData(float.PositiveInfinity)]
	[InlineData(float.NegativeInfinity)]
	public void NonFiniteUpdateInterval_IsRejected(float interval)
	{
		Action create = () => SystemUpdateSettings.FixedInterval(interval);
		create.Should().Throw<ArgumentOutOfRangeException>();
	}

	private static void AssertDisposed(CommandStream commands)
	{
		commands.Should().NotBeNull();
		Action reuse = () => commands.CreateEntity();
		reuse.Should().Throw<ObjectDisposedException>();
	}

	private static World CreateWorld(int entityCapacity) => new(new WorldConfig
	{
		EntityCapacity = entityCapacity, ComponentTypeCapacity = 4, QueryResultCapacity = 4,
		CommandCapacity = 4, CommandPayloadCapacityPerType = 4, ChunkCapacity = 4,
		MaxDegreeOfParallelism = 1
	});

	[NoWorldAccess]
	private sealed class CapturingSystem : ISystem
	{
		public CommandStream? Captured { get; private set; }
		public bool ThrowDuringUpdate { get; set; }
		public void Update(in SystemContext context)
		{
			Captured = context.CommandStream;
			Captured.CreateEntity();
			Captured.CreateEntity();
			if (ThrowDuringUpdate) throw new InvalidOperationException("Update failed.");
		}
	}

	private sealed class IntervalSystem : ISystem
	{
		public int Count { get; private set; }
		public SystemUpdateSettings UpdateSettings => SystemUpdateSettings.FixedInterval(1f);
		public void Update(in SystemContext context) => Count++;
	}
}

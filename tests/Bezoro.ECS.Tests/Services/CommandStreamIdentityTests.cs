using System;
using System.Reflection;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

[Collection("Command stream identity boundaries")]
public sealed class CommandStreamIdentityTests
{
	[Fact]
	public void TemporaryHandles_AvoidSentinelsAndDifferBetweenStreamsAndGenerations()
	{
		using var world = CreateWorld();
		using var first = world.CreateCommandStream();
		using var second = world.CreateCommandStream();
		var old = first.CreateEntity();
		old.Should().NotBe(Entity.None).And.NotBe(Entity.Wildcard);
		second.CreateEntity().Should().NotBe(old);
		world.Playback(first);
		first.CreateEntity().Should().NotBe(old);
		first.Set(old, new Position { Value = 100 });
		Action playback = () => world.Playback(first);
		playback.Should().Throw<InvalidOperationException>().WithMessage("*generation*");
	}

	[Fact]
	public void ForeignTemporaryHandle_CannotAliasALocalCreation()
	{
		using var world = CreateWorld();
		using var first = world.CreateCommandStream();
		using var second = world.CreateCommandStream();
		var foreign = first.CreateEntity();
		second.CreateEntity();
		second.Set(foreign, new Position());
		Action playback = () => world.Playback(second);
		playback.Should().Throw<InvalidOperationException>().WithMessage("*another command stream*");
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(-2)]
	[InlineData(int.MinValue)]
	public void SentinelAndInvalidTemporaryHandles_FailWithoutOverflow(int entityId)
	{
		using var world = CreateWorld();
		using var commands = world.CreateCommandStream();
		commands.CreateEntity();
		commands.Destroy(new Entity(entityId, 0));
		Action playback = () => world.Playback(commands);
		playback.Should().Throw<InvalidOperationException>();
	}

	[Fact]
	public void DroppedPayload_IsIncludedInOverflowDiagnostics()
	{
		using var world = CreateWorld();
		using var commands = world.CreateCommandStream();
		var entity = world.Spawn();
		commands.Set(entity, new Position { Value = 1 });
		commands.Set(entity, new Position { Value = 2 });
		commands.GetDiagnostics().OverflowCount.Should().Be(1);
		world.Playback(commands);
		world.Read<Position>(entity).Value.Should().Be(1);
	}

	[Fact]
	public void TemporaryIdentityBoundary_FailsBeforeReusingAnEarlierGeneration()
	{
		CommandStream.ValidateTemporaryGeneration(1).Should().Be(1);
		CommandStream.ValidateTemporaryGeneration(uint.MaxValue).Should().Be(-1);
		Action exhausted = () => CommandStream.ValidateTemporaryGeneration((long)uint.MaxValue + 1);
		exhausted.Should().Throw<InvalidOperationException>().WithMessage("*identities cannot be recycled*");
	}

	[Fact]
	public void StreamsWithoutTemporaryCreation_DoNotConsumeGlobalIdentityTokens()
	{
		var counter = typeof(CommandStream).GetField("_nextTemporaryGeneration", BindingFlags.NonPublic | BindingFlags.Static)!;
		long before = (long)counter.GetValue(null)!;
		using var world = CreateWorld();
		var entity = world.Spawn(new Position());
		using (var commands = world.CreateCommandStream())
		{
			world.Playback(commands);
			commands.Set(entity, new Position { Value = 1 });
			world.Playback(commands);
			commands.Reset();
		}
		((long)counter.GetValue(null)!).Should().Be(before);
	}

	[Fact]
	public void ExhaustedTemporaryIdentities_RejectBeforeRecordingOrRegisteringComponents()
	{
		var counter = typeof(CommandStream).GetField("_nextTemporaryGeneration", BindingFlags.NonPublic | BindingFlags.Static)!;
		long saved = (long)counter.GetValue(null)!;
		try
		{
			counter.SetValue(null, (long)uint.MaxValue);
			using var world = CreateWorld();
			using var commands = world.CreateCommandStream();
			Action create = () => commands.CreateEntity(new Position());
			create.Should().Throw<InvalidOperationException>().WithMessage("*exhausted temporary*");
			commands.HasCommands.Should().BeFalse();
			((long)counter.GetValue(null)!).Should().Be(uint.MaxValue);
			world.EntityCount.Should().Be(0);
			world.GetDiagnostics().ComponentTypeArena.Used.Should().Be(0);
		}
		finally { counter.SetValue(null, saved); }
	}

	private static World CreateWorld() => new(new WorldConfig
	{
		EntityCapacity = 8, ComponentTypeCapacity = 8, QueryResultCapacity = 8,
		CommandCapacity = 8, CommandPayloadCapacityPerType = 1, ChunkCapacity = 2,
		OverflowPolicy = WorldOverflowPolicy.DropNewest
	});
	private struct Position { public int Value; }
}

[CollectionDefinition("Command stream identity boundaries", DisableParallelization = true)]
public sealed class CommandStreamIdentityBoundaryCollection;

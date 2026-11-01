using System;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

public sealed class WorldExecutionLifetimeTests
{
	[Fact]
	public void CursorCopies_ShareDisposalAndCannotReleaseTheNextExecution()
	{
		using var world = CreateWorld();
		world.Spawn(new Position { Value = 7 });
		var handle = world.Compile<Positions>();
		var first = world.Execute(handle);
		first.MoveNext();
		var copy = first;
		first.Dispose();
		Action readCopy = () => copy.Read<Position>(0);
		readCopy.Should().Throw<ObjectDisposedException>();

		using (var next = world.Execute(handle))
		{
			next.MoveNext();
			copy.Dispose();
			Action reset = world.Reset;
			reset.Should().Throw<InvalidOperationException>();
			next.Read<Position>(0).Value.Should().Be(7);
		}
		world.Reset();
	}

	[Fact]
	public void DefaultCursor_IsSafelyDisposableAndRejectsUse()
	{
		var cursor = default(QueryCursor);
		cursor.Dispose();
		Action advance = () => cursor.MoveNext();
		advance.Should().Throw<ObjectDisposedException>();
	}

	[Fact]
	public void DefaultHandle_ExplainsThatCompilationIsRequired()
	{
		using var world = CreateWorld();
		Action execute = () => world.Execute(default(QueryHandle<Positions>));
		Action diagnostics = () => world.GetQueryDiagnostics(default(QueryHandle<Positions>));
		execute.Should().Throw<InvalidOperationException>().WithMessage("*uninitialized*");
		diagnostics.Should().Throw<InvalidOperationException>().WithMessage("*uninitialized*");
	}

	[Fact]
	public void IndexedEntityAccess_TraversesChunksAndArchetypesWithoutMaterializingCurrent()
	{
		using var world = CreateWorld();
		var expected = new Entity[6];
		for (var index = 0; index < expected.Length; index++)
		{
			expected[index] = world.Spawn(new Position { Value = index });
			if (index % 2 == 0) world.Add(expected[index], new Velocity());
		}
		using var cursor = world.Execute(world.Compile<Positions>());
		cursor.Count.Should().Be(6);
		cursor.MoveNext();
		for (int index = cursor.Count - 1; index >= 0; index--)
			cursor.GetEntity(index).Should().Be(expected[cursor.Read<Position>(index).Value]);
		Action before = () => cursor.GetEntity(-1);
		Action after = () => cursor.GetEntity(cursor.Count);
		before.Should().Throw<ArgumentOutOfRangeException>();
		after.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Theory]
	[InlineData("spawn")]
	[InlineData("despawn")]
	[InlineData("add")]
	[InlineData("remove")]
	[InlineData("add relation")]
	[InlineData("remove relation")]
	[InlineData("dispose")]
	public void ActiveCursor_RejectsInvalidatingOperationsBeforeChangingStorage(string operation)
	{
		using var world = CreateWorld();
		var entity = world.Spawn(new Position { Value = 7 });
		var target = world.Spawn();
		world.AddRelation<Link>(entity, target);
		using var cursor = world.Execute(world.Compile<Positions>());
		cursor.MoveNext();
		Action mutate = operation switch
		{
			"spawn" => () => world.Spawn(),
			"despawn" => () => world.Despawn(entity),
			"add" => () => world.Add(entity, new Velocity()),
			"remove" => () => world.Remove<Position>(entity),
			"add relation" => () => world.AddRelation<OtherLink>(entity, target),
			"remove relation" => () => world.RemoveRelation<Link>(entity, target),
			_ => world.Dispose
		};
		mutate.Should().Throw<InvalidOperationException>();
		world.EntityCount.Should().Be(2);
		world.Has<Velocity>(entity).Should().BeFalse();
		world.HasRelation<Link>(entity, target).Should().BeTrue();
		cursor.Read<Position>(0).Value.Should().Be(7);
	}

	[Fact]
	public void QueryCallback_RejectsDisposalWhileItsNativeReferenceIsBorrowed()
	{
		using var world = CreateWorld();
		var entity = world.Spawn(new Position { Value = 7 });
		world.Query<Positions>().ForEach<Position>((Entity _, ref Position position) =>
		{
			Action dispose = world.Dispose;
			dispose.Should().Throw<InvalidOperationException>();
			position.Value++;
		});
		world.Read<Position>(entity).Value.Should().Be(8);
	}

	[Fact]
	public void ActiveCursor_AllowsReplacingAnExistingComponent()
	{
		using var world = CreateWorld();
		var entity = world.Spawn(new Position());
		using var cursor = world.Execute(world.Compile<Positions>());
		cursor.MoveNext();
		world.Set(entity, new Position { Value = 12 });
		cursor.Read<Position>(0).Value.Should().Be(12);
	}

	[Fact]
	public void CursorCallback_CannotDisposeAnotherCopyOfItsOwnExecution()
	{
		using var world = CreateWorld();
		var entity = world.Spawn(new Position());
		var cursor = world.Execute(world.Compile<Positions>());
		var copy = cursor;
		try
		{
			cursor.MoveNext();
			cursor.ForEach<Position>((ref Position position) =>
			{
				Action disposeCopy = copy.Dispose;
				disposeCopy.Should().Throw<InvalidOperationException>();
				position.Value++;
			});
			world.Read<Position>(entity).Value.Should().Be(1);
		}
		finally { cursor.Dispose(); copy.Dispose(); }
		world.Reset();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void QueryJobs_ProtectStorageUntilTheJobReturns(bool parallel)
	{
		using var world = CreateWorld();
		var entity = world.Spawn(new Position());
		var query = world.Query<Positions>();
		var job = new LifetimeJob(world);
		if (parallel) query.RunParallel<LifetimeJob, Position>(job, 2);
		else query.Run<LifetimeJob, Position>(job);
		world.Read<Position>(entity).Value.Should().Be(1);
		world.Reset();
	}

	[Fact]
	public void SpawnWithInsufficientComponentCapacity_DoesNotCreateAPartialEntity()
	{
		using var world = new World(new WorldConfig { ComponentTypeCapacity = 1 });
		Action spawn = () => world.Spawn(new Position(), new Velocity());
		spawn.Should().Throw<InvalidOperationException>();
		world.EntityCount.Should().Be(0);
	}

	[Fact]
	public void MissingComponentProbes_DoNotConsumeTypeCapacity()
	{
		using var world = new World(new WorldConfig { ComponentTypeCapacity = 1 });
		var entity = world.Spawn(new Position());
		world.Has<Velocity>(entity).Should().BeFalse();
		world.TryRead<Velocity>(entity, out _).Should().BeFalse();
		world.TryWrite<Velocity>(entity, out _).Should().BeFalse();
		world.Remove<Velocity>(entity);
		world.GetDiagnostics().ComponentTypeArena.Used.Should().Be(1);
	}

	[Fact]
	public void ResourceFactory_CannotDisposeOrClearItsWorldBeforeRegistration()
	{
		using var world = CreateWorld();
		world.Spawn(new Position());
		world.GetOrCreateResource(() =>
		{
			Action dispose = world.Dispose;
			Action clear = world.Clear;
			dispose.Should().Throw<InvalidOperationException>();
			clear.Should().Throw<InvalidOperationException>();
			return new object();
		});
		world.EntityCount.Should().Be(1);
		world.HasResource<object>().Should().BeTrue();
	}

	private readonly struct LifetimeJob(World world) : IForEach<Position>
	{
		public void Execute(ref Position position)
		{
			Action dispose = world.Dispose;
			dispose.Should().Throw<InvalidOperationException>();
			Action clear = world.Clear;
			clear.Should().Throw<InvalidOperationException>();
			position.Value++;
		}
	}

	private static World CreateWorld() => new(new WorldConfig
	{
		EntityCapacity = 8, ComponentTypeCapacity = 8, QueryResultCapacity = 8,
		CommandCapacity = 8, CommandPayloadCapacityPerType = 8, ChunkCapacity = 2
	});
	private struct Position { public int Value; }
	private struct Velocity;
	private struct Link;
	private struct OtherLink;
	private readonly struct Positions : ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder) => builder.With<Position>();
	}
}

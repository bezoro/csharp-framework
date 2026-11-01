using System;
using System.Collections.Generic;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using JetBrains.Annotations;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

[TestSubject(typeof(World))]
public class WorldAdvancedApiTests
{
	private static readonly Entity ExistingEntity = new(0, 0);

	[Fact]
	public void CaptureAndRestoreSnapshot_WhenWorldHasRelationsResourcesAndComponents_ShouldRoundTrip()
	{
		using var source = new World(
			new WorldConfig
			{
				EntityCapacity                = 64,
				ComponentTypeCapacity         = 32,
				CommandCapacity               = 256,
				CommandPayloadCapacityPerType = 256,
				QueryResultCapacity           = 64
			}
		);

		source.SetResource(new SimulationSettings { Gravity = 9.81f });
		source.SetResource(new SnapshotTagResource("alpha"));

		var target   = source.Spawn(new Position { X = 10, Y = 20 });
		var follower = source.Spawn(new Position { X = 2, Y  = 3 }, new Velocity { X = 1, Y = 0 });
		source.AddRelation<Follows>(follower, target);

		var writer = new InMemorySnapshotWriter();
		source.CaptureSnapshot(ref writer);

		using var restored = new World(
			new WorldConfig
			{
				EntityCapacity                = 64,
				ComponentTypeCapacity         = 32,
				CommandCapacity               = 256,
				CommandPayloadCapacityPerType = 256,
				QueryResultCapacity           = 64
			}
		);

		var reader = new InMemorySnapshotReader(writer.Captured);
		restored.RestoreSnapshot(
			ref reader,
			new()
			{
				AllowedComponentTypes = [typeof(Position), typeof(Velocity)],
				AllowedRelationTypes  = [typeof(Follows)],
				AllowedResourceTypes  = [typeof(SimulationSettings), typeof(SnapshotTagResource)]
			}
		);

		restored.ReadResource<SimulationSettings>().Gravity.Should().Be(9.81f);
		restored.ReadResource<SnapshotTagResource>().Tag.Should().Be("alpha");

		var handle    = restored.Compile<PositionQuerySpec>();
		var positions = new List<Position>(2);
		using (var cursor = restored.Execute(handle))
		{
			cursor.MoveNext().Should().BeTrue();
			cursor.Current.Length.Should().Be(2);
			for (var i = 0; i < cursor.Current.Length; i++)
				positions.Add(cursor.Write<Position>(i));
		}

		positions.Should().Contain(new Position { X = 10, Y = 20 });
		positions.Should().Contain(new Position { X = 2, Y  = 3 });

		var       relationHandle = restored.Compile<FollowerRelationQuerySpec>();
		using var relationCursor = restored.Execute(relationHandle);
		relationCursor.MoveNext().Should().BeTrue();
		relationCursor.Current.Length.Should().Be(1);
	}

	[Fact]
	public void GetQueryDiagnostics_WhenQueryIsCompiled_ShouldReturnFilterAndMatchCounts()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 32,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 64,
				CommandPayloadCapacityPerType = 64,
				QueryResultCapacity           = 32
			}
		);

		using var commands = world.CreateCommandStream();
		var       first    = commands.CreateEntity();
		commands.Set(first, new Position { X = 1, Y = 1 });
		var second = commands.CreateEntity();
		commands.Set(second, new Position { X = 2, Y = 2 });
		commands.Set(second, new Velocity { X = 5, Y = 5 });
		var third = commands.CreateEntity();
		commands.Set(third, new Position { X = 3, Y = 3 });
		world.Playback(commands);

		var handle      = world.Compile<PositionWithoutVelocityQuerySpec>();
		var diagnostics = world.GetQueryDiagnostics(handle);

		diagnostics.MatchingEntityCount.Should().Be(2);
		diagnostics.MatchingChunkCount.Should().BeGreaterThan(0);
		diagnostics.MatchingArchetypeCount.Should().BeGreaterThan(0);
		diagnostics.AllTypes.Should().ContainSingle(static type => type == typeof(Position));
		diagnostics.NoneTypes.Should().ContainSingle(static type => type == typeof(Velocity));
		diagnostics.RelatedRelationType.Should().BeNull();
	}

	[Fact]
	public void GetQueryDiagnostics_WhenUsingChangedFilter_ShouldNotAdvanceIncrementalWindow()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 16,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 32,
				CommandPayloadCapacityPerType = 32,
				QueryResultCapacity           = 16
			}
		);

		using var commands = world.CreateCommandStream();
		var       entity   = commands.CreateEntity();
		commands.Set(entity, new Position { X = 8, Y = 9 });
		world.Playback(commands);

		var handle      = world.Compile<ChangedPositionQuerySpec>();
		var diagnostics = world.GetQueryDiagnostics(handle);
		diagnostics.MatchingEntityCount.Should().Be(1);

		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();
		cursor.Current.Length.Should().Be(1);
	}

	[Fact]
	public void RestoreSnapshot_WhenAllowAllFlagsAreEnabled_ShouldPreserveTrustedSameProcessScenario()
	{
		using var source = CreateWorldWithExistingState();
		source.SetResource(new SnapshotTagResource("trusted"));
		var writer = new InMemorySnapshotWriter();
		source.CaptureSnapshot(ref writer);

		using var restored = new World(
			new WorldConfig
			{
				EntityCapacity                = 8,
				ComponentTypeCapacity         = 8,
				CommandCapacity               = 16,
				CommandPayloadCapacityPerType = 16,
				QueryResultCapacity           = 8
			}
		);

		var reader = new InMemorySnapshotReader(writer.Captured);
		restored.RestoreSnapshot(
			ref reader,
			new()
			{
				AllowAllComponentTypes = true,
				AllowAllRelationTypes  = true,
				AllowAllResourceTypes  = true
			}
		);

		restored.ReadResource<SimulationSettings>().Gravity.Should().Be(9.81f);
		restored.ReadResource<SnapshotTagResource>().Tag.Should().Be("trusted");
	}

	[Fact]
	public void CaptureSnapshot_WhenQueryViewCallbackIsActive_ShouldRejectUntilIterationCompletes()
	{
		using var world = new World();
		world.Spawn(new Position { X = 1, Y = 2 });
		var writer = new InMemorySnapshotWriter();

		world.Query<PositionQuerySpec>().ForEach<Position>(
			(Entity _, ref Position _) =>
			{
				var act = () => world.CaptureSnapshot(ref writer);

				act.Should().Throw<InvalidOperationException>()
				   .WithMessage("Snapshot capture cannot run while a query iteration is active.");
			}
		);

		world.CaptureSnapshot(ref writer);
		writer.Captured.Entities.Should().ContainSingle();
	}

	[Fact]
	public void RestoreSnapshot_WhenQueryViewCallbackIsActive_ShouldRejectUntilIterationCompletes()
	{
		using var world = new World();
		world.Spawn(new Position { X = 1, Y = 2 });
		var reader = new InMemorySnapshotReader(CreateSnapshot());

		world.Query<PositionQuerySpec>().ForEach<Position>(
			(Entity _, ref Position _) =>
			{
				var act = () => world.RestoreSnapshot(ref reader);

				act.Should().Throw<InvalidOperationException>()
				   .WithMessage("Snapshot restore cannot run while a query iteration is active.");
			}
		);

		world.RestoreSnapshot(ref reader);
		world.EntityCount.Should().Be(0);
	}

	[Fact]
	public void RestoreSnapshot_WhenComponentTypeIsNotAllowListed_ShouldThrowAndLeaveWorldUnchanged()
	{
		using var world = CreateWorldWithExistingState();
		var snapshot = CreateSnapshot(
			entities:
			[
				new(
					new(12, 1),
					[
						new(typeof(Position), new Position { X = 1, Y = 2 })
					]
				)
			]
		);

		var reader = new InMemorySnapshotReader(snapshot);
		var act = () => world.RestoreSnapshot(
			ref reader,
			new()
			{
				AllowedResourceTypes = [typeof(SimulationSettings)]
			}
		);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*component type*not allow-listed*");

		AssertWorldPreserved(world);
	}

	[Fact]
	public void RestoreSnapshot_WhenDuplicateEntityRecordsExist_ShouldThrowAndLeaveWorldUnchanged()
	{
		using var world           = CreateWorldWithExistingState();
		var       duplicateEntity = new Entity(42, 3);
		var snapshot = CreateSnapshot(
			entities:
			[
				new(duplicateEntity, [new(typeof(Position), new Position { X = 1, Y = 2 })]),
				new(duplicateEntity, [new(typeof(Position), new Position { X = 3, Y = 4 })])
			]
		);

		var reader = new InMemorySnapshotReader(snapshot);
		var act = () => world.RestoreSnapshot(
			ref reader,
			new()
			{
				AllowedComponentTypes = [typeof(Position)]
			}
		);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*Duplicate snapshot entity*");

		AssertWorldPreserved(world);
	}

	[Fact]
	public void RestoreSnapshot_WhenDuplicateRelationsExist_ShouldThrowAndLeaveWorldUnchanged()
	{
		using var world  = CreateWorldWithExistingState();
		var       source = new Entity(31, 1);
		var       target = new Entity(32, 1);
		var snapshot = CreateSnapshot(
			entities:
			[
				new(source, []),
				new(target, [])
			],
			relations:
			[
				new(typeof(Follows), source, target),
				new(typeof(Follows), source, target)
			]
		);

		var reader = new InMemorySnapshotReader(snapshot);
		var act = () => world.RestoreSnapshot(
			ref reader,
			new()
			{
				AllowedRelationTypes = [typeof(Follows)]
			}
		);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*Duplicate snapshot relation*");

		AssertWorldPreserved(world);
	}

	[Fact]
	public void RestoreSnapshot_WhenDuplicateResourceTypesExist_ShouldThrowAndLeaveWorldUnchanged()
	{
		using var world = CreateWorldWithExistingState();
		var snapshot = CreateSnapshot(
			[
				new(typeof(SimulationSettings), new SimulationSettings { Gravity = 1 }),
				new(typeof(SimulationSettings), new SimulationSettings { Gravity = 2 })
			]
		);

		var reader = new InMemorySnapshotReader(snapshot);
		var act = () => world.RestoreSnapshot(
			ref reader,
			new()
			{
				AllowedResourceTypes = [typeof(SimulationSettings)]
			}
		);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*Duplicate snapshot resource type*");

		AssertWorldPreserved(world);
	}

	[Fact]
	public void RestoreSnapshot_WhenEntityIdentityIsEntityNone_ShouldThrowAndLeaveWorldUnchanged()
	{
		using var world = CreateWorldWithExistingState();
		var snapshot = CreateSnapshot(
			entities:
			[
				new(Entity.None, [new(typeof(Position), new Position { X = 9, Y = 9 })])
			]
		);

		var reader = new InMemorySnapshotReader(snapshot);
		var act = () => world.RestoreSnapshot(
			ref reader,
			new()
			{
				AllowedComponentTypes = [typeof(Position)]
			}
		);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*Entity.None*");

		AssertWorldPreserved(world);
	}

	[Fact]
	public void RestoreSnapshot_WhenNoTypesAreAllowedByDefault_ShouldRejectBeforeMutatingWorld()
	{
		using var world               = CreateWorldWithExistingState();
		int       existingEntityCount = world.EntityCount;
		float     existingGravity     = world.ReadResource<SimulationSettings>().Gravity;
		var snapshot = CreateSnapshot(
			[
				new(typeof(SnapshotTagResource), new SnapshotTagResource("unsafe"))
			],
			[
				new(
					new(11, 1),
					[
						new(typeof(Position), new Position { X = 4, Y = 7 })
					]
				)
			]
		);

		var reader = new InMemorySnapshotReader(snapshot);
		var act    = () => world.RestoreSnapshot(ref reader);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*not allow-listed*");

		world.EntityCount.Should().Be(existingEntityCount);
		world.ReadResource<SimulationSettings>().Gravity.Should().Be(existingGravity);
		world.Read<Position>(ExistingEntity).Should().Be(new Position { X = 99, Y = 100 });
	}

	[Fact]
	public void RestoreSnapshot_WhenRelationReferencesMissingEntity_ShouldThrowAndLeaveWorldUnchanged()
	{
		using var world = CreateWorldWithExistingState();
		var snapshot = CreateSnapshot(
			entities:
			[
				new(new(51, 1), [])
			],
			relations:
			[
				new(typeof(Follows), new(51, 1), new(52, 1))
			]
		);

		var reader = new InMemorySnapshotReader(snapshot);
		var act = () => world.RestoreSnapshot(
			ref reader,
			new()
			{
				AllowedRelationTypes = [typeof(Follows)]
			}
		);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*target*was not found*");

		AssertWorldPreserved(world);
	}

	[Fact]
	public void RestoreSnapshot_WhenRelationTypeIsNotAllowListed_ShouldThrowAndLeaveWorldUnchanged()
	{
		using var world = CreateWorldWithExistingState();
		var snapshot = CreateSnapshot(
			entities:
			[
				new(new(21, 1), []),
				new(new(22, 1), [])
			],
			relations:
			[
				new(typeof(Follows), new(21, 1), new(22, 1))
			]
		);

		var reader = new InMemorySnapshotReader(snapshot);
		var act = () => world.RestoreSnapshot(
			ref reader,
			new()
			{
				AllowAllResourceTypes = true
			}
		);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*relation type*not allow-listed*");

		AssertWorldPreserved(world);
	}

	[Fact]
	public void RestoreSnapshot_WhenRelationUsesWildcardTarget_ShouldThrowAndLeaveWorldUnchanged()
	{
		using var world  = CreateWorldWithExistingState();
		var       source = new Entity(61, 1);
		var snapshot = CreateSnapshot(
			entities:
			[
				new(source, [])
			],
			relations:
			[
				new(typeof(Follows), source, Entity.Wildcard)
			]
		);

		var reader = new InMemorySnapshotReader(snapshot);
		var act = () => world.RestoreSnapshot(
			ref reader,
			new()
			{
				AllowedRelationTypes = [typeof(Follows)]
			}
		);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*concrete entities*");

		AssertWorldPreserved(world);
	}

	[Fact]
	public void RestoreSnapshot_WhenResourceTypeIsNotAllowListed_ShouldThrowAndLeaveWorldUnchanged()
	{
		var snapshot = new WorldSnapshot(
			new[]
			{
				new SnapshotResourceRecord(typeof(SnapshotTagResource), new SnapshotTagResource("unsafe"))
			},
			Array.Empty<SnapshotEntityRecord>(),
			Array.Empty<SnapshotRelationRecord>()
		);

		using var world  = CreateWorldWithExistingState();
		var       reader = new InMemorySnapshotReader(snapshot);
		var act = () => world.RestoreSnapshot(
			ref reader,
			new()
			{
				AllowAllComponentTypes = true,
				AllowAllRelationTypes  = true
			}
		);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*resource type*not allow-listed*");

		AssertWorldPreserved(world);
	}

	[Fact]
	public void RestoreSnapshot_WhenSnapshotExceedsEntityCapacity_ShouldThrowBeforeClear()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 1,
				ComponentTypeCapacity         = 8,
				CommandCapacity               = 16,
				CommandPayloadCapacityPerType = 16,
				QueryResultCapacity           = 8
			}
		);

		world.SetResource(new SimulationSettings { Gravity = 9.81f });
		var original = world.Spawn(new Position { X = 99, Y = 100 });
		var snapshot = CreateSnapshot(
			entities:
			[
				new(new(71, 1), []),
				new(new(72, 1), [])
			]
		);

		var reader = new InMemorySnapshotReader(snapshot);
		var act    = () => world.RestoreSnapshot(ref reader, new() { AllowAllComponentTypes = true });

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*entity capacity*");

		world.EntityCount.Should().Be(1);
		world.ReadResource<SimulationSettings>().Gravity.Should().Be(9.81f);
		world.Read<Position>(original).Should().Be(new Position { X = 99, Y = 100 });
	}

	[Fact]
	public void RestoreSnapshot_WhenTypeValidatorRejectsAllowedType_ShouldThrowAndLeaveWorldUnchanged()
	{
		using var world = CreateWorldWithExistingState();
		var snapshot = CreateSnapshot(
			entities:
			[
				new(new(91, 1), [new(typeof(Position), new Position { X = 5, Y = 6 })])
			]
		);

		var reader = new InMemorySnapshotReader(snapshot);
		var act = () => world.RestoreSnapshot(
			ref reader,
			new()
			{
				AllowedComponentTypes = [typeof(Position)],
				TypeValidator         = static type => type != typeof(Position)
			}
		);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*not allowed*");

		AssertWorldPreserved(world);
	}

	[Fact]
	public void RestoreSnapshot_WhenUniqueSnapshotTypesExceedComponentTypeCapacity_ShouldThrowBeforeClear()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 8,
				ComponentTypeCapacity         = 1,
				CommandCapacity               = 16,
				CommandPayloadCapacityPerType = 16,
				QueryResultCapacity           = 8
			}
		);

		var original = world.Spawn(new Position { X = 99, Y = 100 });
		world.SetResource(new SimulationSettings { Gravity = 9.81f });
		var snapshot = CreateSnapshot(
			entities:
			[
				new(new(81, 1), [new(typeof(Position), new Position { X = 1, Y = 2 })])
			],
			relations:
			[
				new(typeof(Follows), new(81, 1), new(81, 1))
			]
		);

		var reader = new InMemorySnapshotReader(snapshot);
		var act = () => world.RestoreSnapshot(
			ref reader,
			new()
			{
				AllowedComponentTypes = [typeof(Position)],
				AllowedRelationTypes  = [typeof(Follows)]
			}
		);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*component type capacity*");

		world.EntityCount.Should().Be(1);
		world.Read<Position>(original).Should().Be(new Position { X = 99, Y = 100 });
	}

	[Fact]
	public void QueryView_RunParallel_WhenCursorIsActive_ShouldAllowIndependentExecution()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 32,
				ComponentTypeCapacity         = 8,
				CommandCapacity               = 64,
				CommandPayloadCapacityPerType = 64,
				QueryResultCapacity           = 32,
				MaxDegreeOfParallelism        = 4
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 8; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y = i });
		}

		world.Playback(commands);

		var       handle = world.Compile<PositionQuerySpec>();
		var       query  = new QueryView<PositionQuerySpec>(world, handle);
		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();

		query.RunParallel<AdvanceJob, Position>(new(), 2);

		cursor.Write<Position>(0).X.Should().Be(1);
	}

	[Fact]
	public void QueryView_RunParallel_WhenDegreeOfParallelismIsInvalid_ShouldThrowArgumentOutOfRangeException()
	{
		using var world  = new World();
		var       handle = world.Compile<PositionQuerySpec>();
		var       query  = new QueryView<PositionQuerySpec>(world, handle);

		var act = () => query.RunParallel<AdvanceJob, Position>(new(), 0);

		act.Should().Throw<ArgumentOutOfRangeException>()
		   .WithParameterName("degreeOfParallelism");
	}

	[Fact]
	public void QueryView_RunParallel_WhenQueryMatches_ShouldProcessAllEntitiesExactlyOnce()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 64,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 256,
				CommandPayloadCapacityPerType = 256,
				QueryResultCapacity           = 64,
				MaxDegreeOfParallelism        = 8
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 32; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y = i });
			commands.Set(entity, new Velocity { X = 2, Y = -3 });
		}

		world.Playback(commands);
		var handle = world.Compile<PositionVelocityQuerySpec>();
		var query  = new QueryView<PositionVelocityQuerySpec>(world, handle);

		query.RunParallel<IntegrateJob, Position, Velocity>(new(4f), 4);

		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();
		cursor.Current.Length.Should().Be(32);
		for (var i = 0; i < cursor.Current.Length; i++)
		{
			var position = cursor.Write<Position>(i);
			position.X.Should().Be(i + 8);
			position.Y.Should().Be(i - 12);
		}
	}

	private static void AssertWorldPreserved(World world)
	{
		world.EntityCount.Should().Be(1);
		world.ReadResource<SimulationSettings>().Gravity.Should().Be(9.81f);
		world.Read<Position>(ExistingEntity).Should().Be(new Position { X = 99, Y = 100 });
	}

	private static World CreateWorldWithExistingState()
	{
		var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 8,
				ComponentTypeCapacity         = 8,
				CommandCapacity               = 16,
				CommandPayloadCapacityPerType = 16,
				QueryResultCapacity           = 8
			}
		);

		world.SetResource(new SimulationSettings { Gravity = 9.81f });
		world.Spawn(new Position { X                       = 99, Y = 100 }).Should().Be(ExistingEntity);
		return world;
	}

	private static WorldSnapshot CreateSnapshot(
		SnapshotResourceRecord[]? resources = null,
		SnapshotEntityRecord[]?   entities  = null,
		SnapshotRelationRecord[]? relations = null) =>
		new(
			resources ?? [],
			entities ?? [],
			relations ?? []
		);

	private struct AdvanceJob : IForEach<Position>
	{
		public void Execute(ref Position component1) => component1.X += 1;
	}

	private readonly struct ChangedPositionQuerySpec : ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder) => builder.Changed<Position>();
	}

	private readonly struct FollowerRelationQuerySpec : ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder)
		{
			builder.All<Position>();
			builder.Related<Follows>();
		}
	}

	private struct Follows;

	private readonly struct InMemorySnapshotReader(WorldSnapshot snapshot) : IWorldSnapshotReader
	{
		public WorldSnapshot Read() => snapshot;
	}

	private struct InMemorySnapshotWriter : IWorldSnapshotWriter
	{
		public WorldSnapshot Captured { get; private set; }

		public void Write(in WorldSnapshot snapshot) => Captured = snapshot;
	}

	private struct IntegrateJob(float dt) : IForEach<Position, Velocity>
	{
		public void Execute(ref Position component1, in Velocity component2)
		{
			component1.X += component2.X * dt;
			component1.Y += component2.Y * dt;
		}
	}

	private struct Position
	{
		public float X;
		public float Y;
	}

	private readonly struct PositionQuerySpec : ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder) => builder.All<Position>();
	}

	private readonly struct PositionVelocityQuerySpec : ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder)
		{
			builder.All<Position>();
			builder.All<Velocity>();
		}
	}

	private readonly struct PositionWithoutVelocityQuerySpec : ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder)
		{
			builder.All<Position>();
			builder.None<Velocity>();
		}
	}

	private struct SimulationSettings
	{
		public float Gravity;
	}

	private sealed record SnapshotTagResource(string Tag);

	private struct Velocity
	{
		public float X;
		public float Y;
	}
}

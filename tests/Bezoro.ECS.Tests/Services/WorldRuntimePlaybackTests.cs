using System;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

public partial class WorldRuntimeTests
{
	[Fact]
	public void Playback_WhenCommandStreamCreatesTemporaryEntity_ShouldResolveAndApplyComponents()
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

		using var commands  = world.CreateCommandStream();
		var       temporary = commands.CreateEntity();
		commands.Set(temporary, new Position { X = 4, Y = 9 });

		world.Playback(commands);

		var       query  = world.Compile<PositionQuerySpec>();
		using var cursor = world.Execute(query);
		cursor.MoveNext().Should().BeTrue();
		cursor.Current.Length.Should().Be(1);
		cursor.Write<Position>(0).Should().Be(new Position { X = 4, Y = 9 });
	}


	[Fact]
	public void Playback_WhenCreateEntityWithComponentIsUsed_ShouldCreateEntityInSingleCommand()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 16,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 16,
				CommandPayloadCapacityPerType = 16,
				QueryResultCapacity           = 16
			}
		);

		using var commands = world.CreateCommandStream();
		commands.CreateEntity(new Position { X = 11, Y = -3 });
		commands.GetDiagnostics().RecordedCommands.Should().Be(1);

		world.Playback(commands);

		var       handle = world.Compile<PositionQuerySpec>();
		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();
		cursor.Current.Length.Should().Be(1);
		cursor.Write<Position>(0).Should().Be(new Position { X = 11, Y = -3 });
	}

	[Fact]
	public void Playback_WhenCreateEntityWithComponentRunContainsManyEntities_ShouldApplyEveryPayload()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 16,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 16,
				CommandPayloadCapacityPerType = 16,
				QueryResultCapacity           = 16
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 4; i++)
			commands.CreateEntity(new Position { X = i, Y = -i });

		world.Playback(commands);

		var       handle = world.Compile<PositionQuerySpec>();
		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();
		cursor.Current.Length.Should().Be(4);
		for (var i = 0; i < cursor.Current.Length; i++)
			cursor.Write<Position>(i).Should().Be(new Position { X = i, Y = -i });
	}


	[Fact]
	public void Playback_WhenDenseRemoveFullyMarksAChunk_ShouldKeepOtherChunkLocationsStable()
	{
		const int ENTITY_COUNT = 8;
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 16,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 32,
				CommandPayloadCapacityPerType = 32,
				QueryResultCapacity           = 16,
				ChunkCapacity                 = 4
			}
		);

		using var create = world.CreateCommandStream();
		for (var i = 0; i < ENTITY_COUNT; i++)
		{
			var temporary = create.CreateEntity(new Position { X = i, Y = i });
			create.Set(temporary, new Velocity { X = 1, Y = -1 });
		}

		world.Playback(create);

		var handle          = world.Compile<PositionQuerySpec>();
		var entitiesByIndex = new Entity[ENTITY_COUNT];
		using (var cursor = world.Execute(handle))
		{
			cursor.MoveNext().Should().BeTrue();
			cursor.Current.Length.Should().Be(ENTITY_COUNT);
			for (var i = 0; i < cursor.Current.Length; i++)
			{
				var entity = cursor.Current[i];
				var index  = (int)world.Read<Position>(entity).X;
				entitiesByIndex[index] = entity;
			}
		}

		using var removeFirstChunk = world.CreateCommandStream();
		for (var i = 0; i < 4; i++)
			removeFirstChunk.Remove<Velocity>(entitiesByIndex[i]);

		world.Playback(removeFirstChunk);

		for (var i = 0; i < ENTITY_COUNT; i++)
		{
			bool shouldBeRemoved = i < 4;
			world.Has<Velocity>(entitiesByIndex[i]).Should().Be(!shouldBeRemoved);
			world.Has<Position>(entitiesByIndex[i]).Should().BeTrue();
		}

		world.EntityCount.Should().Be(ENTITY_COUNT);
	}


	[Fact]
	public void Playback_WhenDenseRemoveMovesEntireSourceArchetypeIntoPartiallyFilledTarget_ShouldKeepLocationsStable()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 24,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 64,
				CommandPayloadCapacityPerType = 64,
				QueryResultCapacity           = 24,
				ChunkCapacity                 = 4
			}
		);

		using var create               = world.CreateCommandStream();
		var       positionOnlyEntities = new Entity[2];
		for (var i = 0; i < positionOnlyEntities.Length; i++)
			positionOnlyEntities[i] = create.CreateEntity(new Position { X = 1_000 + i, Y = 1_000 + i });

		var positionVelocityEntities = new Entity[8];
		for (var i = 0; i < positionVelocityEntities.Length; i++)
		{
			var temporary = create.CreateEntity(new Position { X = i, Y = i });
			create.Set(temporary, new Velocity { X = i + 10, Y = -i });
			positionVelocityEntities[i] = temporary;
		}

		world.Playback(create);

		var allPositionHandle   = world.Compile<PositionQuerySpec>();
		var entitiesByPositionX = new Entity[1_002];
		using (var cursor = world.Execute(allPositionHandle))
		{
			cursor.MoveNext().Should().BeTrue();
			cursor.Current.Length.Should().Be(10);
			for (var i = 0; i < cursor.Current.Length; i++)
			{
				var entity = cursor.Current[i];
				var key    = (int)world.Read<Position>(entity).X;
				entitiesByPositionX[key] = entity;
			}
		}

		using var remove = world.CreateCommandStream();
		for (var i = 0; i < positionVelocityEntities.Length; i++)
			remove.Remove<Velocity>(entitiesByPositionX[i]);

		world.Playback(remove);

		for (var i = 0; i < 8; i++)
		{
			var entity = entitiesByPositionX[i];
			world.Read<Position>(entity).Should().Be(new Position { X = i, Y = i });
			world.Has<Velocity>(entity).Should().BeFalse();
		}

		for (var i = 0; i < positionOnlyEntities.Length; i++)
		{
			var entity = entitiesByPositionX[1_000 + i];
			world.Read<Position>(entity).Should().Be(new Position { X = 1_000 + i, Y = 1_000 + i });
			world.Has<Velocity>(entity).Should().BeFalse();
		}
	}


	[Fact]
	public void Playback_WhenDenseRemovePartiallyMarksChunk_ShouldKeepSurvivorLocationsValid()
	{
		const int ENTITY_COUNT = 8;
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 16,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 32,
				CommandPayloadCapacityPerType = 32,
				QueryResultCapacity           = 16,
				ChunkCapacity                 = 8
			}
		);

		using var create = world.CreateCommandStream();
		for (var i = 0; i < ENTITY_COUNT; i++)
		{
			var temporary = create.CreateEntity(new Position { X = i, Y = i });
			create.Set(temporary, new Velocity { X = i, Y = -i });
		}

		world.Playback(create);

		var handle          = world.Compile<PositionQuerySpec>();
		var entitiesByIndex = new Entity[ENTITY_COUNT];
		using (var cursor = world.Execute(handle))
		{
			cursor.MoveNext().Should().BeTrue();
			cursor.Current.Length.Should().Be(ENTITY_COUNT);
			for (var i = 0; i < cursor.Current.Length; i++)
			{
				var entity = cursor.Current[i];
				var index  = (int)world.Read<Position>(entity).X;
				entitiesByIndex[index] = entity;
			}
		}

		using var remove = world.CreateCommandStream();
		remove.Remove<Velocity>(entitiesByIndex[0]);
		remove.Remove<Velocity>(entitiesByIndex[3]);
		remove.Remove<Velocity>(entitiesByIndex[7]);
		world.Playback(remove);

		for (var i = 0; i < ENTITY_COUNT; i++)
		{
			bool shouldBeRemoved = i is 0 or 3 or 7;
			world.Has<Velocity>(entitiesByIndex[i]).Should().Be(!shouldBeRemoved);
			world.Read<Position>(entitiesByIndex[i]).Should().Be(new Position { X = i, Y = i });
		}

		using var set = world.CreateCommandStream();
		set.Set(entitiesByIndex[1], new Velocity { X = 99, Y = 42 });
		world.Playback(set);
		world.Read<Velocity>(entitiesByIndex[1]).Should().Be(new Velocity { X = 99, Y = 42 });
	}


	[Fact]
	public void Playback_WhenDenseRemoveUsesCollidingEntityIds_ShouldRemoveMarkedComponentsOnly()
	{
		const int ENTITY_COUNT = 80;
		const int BATCH_SIZE   = 16;
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 160,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 32,
				CommandPayloadCapacityPerType = 32,
				QueryResultCapacity           = 160
			}
		);

		using var create = world.CreateCommandStream();
		for (var batchStart = 0; batchStart < ENTITY_COUNT; batchStart += BATCH_SIZE)
		{
			for (int i = batchStart; i < batchStart + BATCH_SIZE; i++)
			{
				var temporary = create.CreateEntity(new Position { X = i, Y = i });
				create.Set(temporary, new Velocity { X = 1, Y = -1 });
			}

			world.Playback(create);
		}

		var handle          = world.Compile<PositionQuerySpec>();
		var entitiesByIndex = new Entity[ENTITY_COUNT];
		using (var cursor = world.Execute(handle))
		{
			cursor.MoveNext().Should().BeTrue();
			cursor.Current.Length.Should().Be(ENTITY_COUNT);
			for (var i = 0; i < cursor.Current.Length; i++)
			{
				var entity = cursor.Current[i];
				var index  = (int)world.Read<Position>(entity).X;
				entitiesByIndex[index] = entity;
			}
		}

		using var remove = world.CreateCommandStream();
		for (var i = 0; i < 16; i++)
			remove.Remove<Velocity>(entitiesByIndex[i]);

		for (var i = 64; i < 80; i++)
			remove.Remove<Velocity>(entitiesByIndex[i]);

		world.Playback(remove);

		for (var i = 0; i < ENTITY_COUNT; i++)
		{
			bool shouldBeRemoved = i < 16 || i >= 64;
			world.Has<Velocity>(entitiesByIndex[i]).Should().Be(!shouldBeRemoved);
			world.Has<Position>(entitiesByIndex[i]).Should().BeTrue();
		}
	}


	[Fact]
	public void Playback_WhenManagedLaneComponentIsRecorded_ShouldBeReadableViaManagedApi()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 8,
				ComponentTypeCapacity         = 8,
				CommandCapacity               = 16,
				CommandPayloadCapacityPerType = 16,
				QueryResultCapacity           = 8
			}
		);

		using var commands = world.CreateCommandStream();
		var       entity   = commands.CreateEntity();
		var       payload  = new Payload("alpha");
		commands.Set(entity, new ManagedTag { Payload = payload });
		world.Playback(commands);

		var       positionQuery = world.Compile<ManagedTagQuerySpec>();
		using var cursor        = world.Execute(positionQuery);
		cursor.MoveNext().Should().BeTrue();

		world.TryRead(cursor.Current[0], out ManagedTag resolved).Should().BeTrue();
		resolved.Payload.Should().BeSameAs(payload);
	}


	[Fact]
	public void Playback_WhenRemoveCommandRecorded_ShouldRemoveComponentFromEntity()
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

		using var createCommands = world.CreateCommandStream();
		var       tempEntity     = createCommands.CreateEntity();
		createCommands.Set(tempEntity, new Position { X = 1, Y = 1 });
		createCommands.Set(tempEntity, new Velocity { X = 2, Y = 2 });
		world.Playback(createCommands);

		var    query = world.Compile<PositionAndVelocityQuerySpec>();
		Entity entity;
		using (var cursor = world.Execute(query))
		{
			cursor.MoveNext().Should().BeTrue();
			entity = cursor.Current[0];
		}

		using var removeCommands = world.CreateCommandStream();
		removeCommands.Remove<Velocity>(entity);
		world.Playback(removeCommands);

		world.Has<Velocity>(entity).Should().BeFalse();
		world.Has<Position>(entity).Should().BeTrue();
	}


	[Fact]
	public void Playback_WhenRemoveCommandsTargetSameEntity_ShouldRemainWithoutComponent()
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

		using var create    = world.CreateCommandStream();
		var       temporary = create.CreateEntity();
		create.Set(temporary, new Position { X = 1, Y = 1 });
		create.Set(temporary, new Velocity { X = 2, Y = 2 });
		world.Playback(create);

		var    handle = world.Compile<PositionQuerySpec>();
		Entity entity;
		using (var cursor = world.Execute(handle))
		{
			cursor.MoveNext().Should().BeTrue();
			entity = cursor.Current[0];
		}

		using var remove = world.CreateCommandStream();
		remove.Remove<Velocity>(entity);
		remove.Remove<Velocity>(entity);
		world.Playback(remove);

		world.Has<Velocity>(entity).Should().BeFalse();
		world.Has<Position>(entity).Should().BeTrue();
	}


	[Fact]
	public void Playback_WhenReusingCommandStreamAcrossManagedBatches_ShouldApplyLatestBatch()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 8,
				ComponentTypeCapacity         = 8,
				CommandCapacity               = 16,
				CommandPayloadCapacityPerType = 16,
				QueryResultCapacity           = 8
			}
		);

		using var commands = world.CreateCommandStream();

		var firstPayload = new Payload("first");
		var firstEntity  = commands.CreateEntity();
		commands.Set(firstEntity, new ManagedTag { Payload = firstPayload });
		world.Playback(commands);

		var secondPayload = new Payload("second");
		var secondEntity  = commands.CreateEntity();
		commands.Set(secondEntity, new ManagedTag { Payload = secondPayload });
		world.Playback(commands);

		var       query  = world.Compile<ManagedTagQuerySpec>();
		using var cursor = world.Execute(query);
		cursor.MoveNext().Should().BeTrue();
		cursor.Current.Length.Should().Be(2);
		world.TryRead(cursor.Current[0], out ManagedTag firstResolved).Should().BeTrue();
		world.TryRead(cursor.Current[1], out ManagedTag secondResolved).Should().BeTrue();
		firstResolved.Payload.Should().BeSameAs(firstPayload);
		secondResolved.Payload.Should().BeSameAs(secondPayload);
	}


	[Fact]
	public void Playback_WhenSetCommandsTargetSameEntity_ShouldApplyLastValue()
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

		using var create    = world.CreateCommandStream();
		var       temporary = create.CreateEntity();
		create.Set(temporary, new Position { X = 1, Y = 1 });
		world.Playback(create);

		var    handle = world.Compile<PositionQuerySpec>();
		Entity entity;
		using (var cursor = world.Execute(handle))
		{
			cursor.MoveNext().Should().BeTrue();
			entity = cursor.Current[0];
		}

		using var updates = world.CreateCommandStream();
		updates.Set(entity, new Position { X = 5, Y = 8 });
		updates.Set(entity, new Position { X = 9, Y = 3 });
		world.Playback(updates);

		var position = world.Read<Position>(entity);
		position.Should().Be(new Position { X = 9, Y = 3 });
	}

	[Fact]
	public void ApplySetFromCommandKnownTransition_WhenSourceArchetypeLacksComponent_ShouldThrowBeforeMutation()
	{
		using var world = new World();
		var entity = world.Spawn(new Position { X = 1, Y = 2 });
		int velocityTypeId = world.GetOrCreateComponentTypeId<Velocity>();
		world.DescribeSetTransition(entity, velocityTypeId, out int sourceArchetypeId, out _);
		var velocity = new Velocity { X = 3, Y = 4 };

		var act = () => world.ApplySetFromCommandKnownTransition(
			entity,
			in velocity,
			velocityTypeId,
			sourceArchetypeId,
			sourceArchetypeId
		);

		act.Should().ThrowExactly<InvalidOperationException>()
		   .WithMessage($"Type id '{velocityTypeId}' does not exist in archetype '{sourceArchetypeId}'.");
		world.Read<Position>(entity).Should().Be(new Position { X = 1, Y = 2 });
		world.Has<Velocity>(entity).Should().BeFalse();
	}

	[Fact]
	public void Playback_WhenFastSetBatchOverwritesExistingComponents_ShouldMarkChangedWithoutMarkingAdded()
	{
		using var world = new World();
		var first = world.Spawn(new Position { X = 1, Y = 2 });
		var second = world.Spawn(new Position { X = 3, Y = 4 });
		var changedHandle = world.Compile<ChangedPositionQuerySpec>();
		var addedHandle = world.Compile<AddedPositionQuerySpec>();

		using (var initialChanged = world.Execute(changedHandle))
		{
			initialChanged.MoveNext().Should().BeTrue();
			initialChanged.Current.ToArray().Should().BeEquivalentTo([first, second]);
		}

		using (var initialAdded = world.Execute(addedHandle))
		{
			initialAdded.MoveNext().Should().BeTrue();
			initialAdded.Current.ToArray().Should().BeEquivalentTo([first, second]);
		}

		using (var unchanged = world.Execute(changedHandle))
		{
			unchanged.MoveNext().Should().BeTrue();
			unchanged.Current.Length.Should().Be(0);
		}

		using (var notAdded = world.Execute(addedHandle))
		{
			notAdded.MoveNext().Should().BeTrue();
			notAdded.Current.Length.Should().Be(0);
		}

		using var updates = world.CreateCommandStream();
		updates.Set(first, new Position { X = 11, Y = 12 });
		updates.Set(second, new Position { X = 13, Y = 14 });
		world.Playback(updates);

		world.Read<Position>(first).Should().Be(new Position { X = 11, Y = 12 });
		world.Read<Position>(second).Should().Be(new Position { X = 13, Y = 14 });
		using (var changed = world.Execute(changedHandle))
		{
			changed.MoveNext().Should().BeTrue();
			changed.Current.ToArray().Should().BeEquivalentTo([first, second]);
		}

		using (var added = world.Execute(addedHandle))
		{
			added.MoveNext().Should().BeTrue();
			added.Current.Length.Should().Be(0);
		}

		using var unchangedAgain = world.Execute(changedHandle);
		unchangedAgain.MoveNext().Should().BeTrue();
		unchangedAgain.Current.Length.Should().Be(0);
	}


	[Fact]
	public void Playback_WhenSetRunContainsDifferentTransitions_ShouldApplyAllCommands()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 16,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 64,
				CommandPayloadCapacityPerType = 64,
				QueryResultCapacity           = 16
			}
		);

		using var create         = world.CreateCommandStream();
		var       firstTemporary = create.CreateEntity();
		create.Set(firstTemporary, new Position { X = 1, Y = 1 });

		var secondTemporary = create.CreateEntity();
		create.Set(secondTemporary, new Position { X = 2, Y = 2 });
		create.Set(secondTemporary, new Velocity { X = 3, Y = 4 });
		world.Playback(create);

		var    positionOnly = world.Compile<PositionQuerySpec>();
		Entity first        = default;
		Entity second       = default;
		using (var cursor = world.Execute(positionOnly))
		{
			cursor.MoveNext().Should().BeTrue();
			for (var i = 0; i < cursor.Current.Length; i++)
			{
				var entity   = cursor.Current[i];
				var position = world.Read<Position>(entity);
				if (position.X == 1)
					first = entity;
				else if (position.X == 2)
					second = entity;
			}
		}

		world.IsAlive(first).Should().BeTrue();
		world.IsAlive(second).Should().BeTrue();

		using var set = world.CreateCommandStream();
		set.Set(first,  new Velocity { X = 10, Y = 20 });
		set.Set(second, new Velocity { X = 30, Y = 40 });
		world.Playback(set);

		world.Read<Velocity>(first).Should().Be(new Velocity { X  = 10, Y = 20 });
		world.Read<Velocity>(second).Should().Be(new Velocity { X = 30, Y = 40 });
		world.Read<Position>(first).Should().Be(new Position { X  = 1, Y  = 1 });
		world.Read<Position>(second).Should().Be(new Position { X = 2, Y  = 2 });
	}


	[Fact]
	public void Playback_WhenStreamBelongsToDifferentWorld_ShouldThrow()
	{
		using var worldA = new World(
			new WorldConfig
			{
				EntityCapacity                = 8,
				ComponentTypeCapacity         = 8,
				CommandCapacity               = 8,
				CommandPayloadCapacityPerType = 8,
				QueryResultCapacity           = 8
			}
		);

		using var worldB = new World(
			new WorldConfig
			{
				EntityCapacity                = 8,
				ComponentTypeCapacity         = 8,
				CommandCapacity               = 8,
				CommandPayloadCapacityPerType = 8,
				QueryResultCapacity           = 8
			}
		);

		using var foreign = worldA.CreateCommandStream();
		var       act     = () => worldB.Playback(foreign);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*different world*");
	}
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

public partial class WorldRuntimeTests
{
	[Fact]
	public async Task Compile_WhenSameColdSpecIsCompiledConcurrently_ShouldBuildOnceAndReturnUsableHandles()
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

		var probe = new CoordinatedQueryCompileProbe();
		CoordinatedPositionQuerySpec.Probe = probe;
		Task<QueryHandle<CoordinatedPositionQuerySpec>>? firstCompile  = null;
		Task<QueryHandle<CoordinatedPositionQuerySpec>>? secondCompile = null;
		var secondBuildEntered = false;
		try
		{
			firstCompile = Task.Factory.StartNew(
				world.Compile<CoordinatedPositionQuerySpec>,
				CancellationToken.None,
				TaskCreationOptions.LongRunning,
				TaskScheduler.Default
			);
			probe.FirstBuildEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

			secondCompile = Task.Factory.StartNew(
				() =>
				{
					probe.SecondCompileStarting.Set();
					return world.Compile<CoordinatedPositionQuerySpec>();
				},
				CancellationToken.None,
				TaskCreationOptions.LongRunning,
				TaskScheduler.Default
			);
			probe.SecondCompileStarting.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
			secondBuildEntered = probe.SecondBuildEntered.Wait(TimeSpan.FromSeconds(5));
		}
		finally
		{
			probe.ReleaseFirstBuild.Set();
			try
			{
				if (firstCompile is not null && secondCompile is not null)
					await Task.WhenAll(firstCompile, secondCompile);
				else if (firstCompile is not null)
					await firstCompile;
			}
			finally
			{
				CoordinatedPositionQuerySpec.Probe = null;
				probe.Dispose();
			}
		}

		using (new AssertionScope())
		{
			secondBuildEntered.Should().BeFalse();
			probe.BuildCount.Should().Be(1);
		}

		var entity = world.Spawn(new Position { X = 1, Y = 2 });
		using (var firstCursor = world.Execute(await firstCompile!))
		{
			firstCursor.MoveNext().Should().BeTrue();
			firstCursor.Current.Length.Should().Be(1);
			firstCursor.Current[0].Should().Be(entity);
		}

		using var secondCursor = world.Execute(await secondCompile!);
		secondCursor.MoveNext().Should().BeTrue();
		secondCursor.Current.Length.Should().Be(1);
		secondCursor.Current[0].Should().Be(entity);
	}

	[Fact]
	public void Compile_WhenDifferentSpecTypesAreCached_ShouldKeepDistinctQueryPlans()
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
		commands.Set(entity, new Position { X = 1, Y = 1 });
		world.Playback(commands);

		var positionOnly        = world.Compile<PositionQuerySpec>();
		var positionAndVelocity = world.Compile<PositionAndVelocityQuerySpec>();

		using (var positionCursor = world.Execute(positionOnly))
		{
			positionCursor.MoveNext().Should().BeTrue();
			positionCursor.Current.Length.Should().Be(1);
		}

		using var positionVelocityCursor = world.Execute(positionAndVelocity);
		positionVelocityCursor.MoveNext().Should().BeTrue();
		positionVelocityCursor.Current.Length.Should().Be(0);
	}


	[Fact]
	public void Execute_WhenCompiledQueryIsCachedBeforeMatchingArchetypeExists_ShouldRefreshAfterStructuralChange()
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

		var handle = world.Compile<PositionQuerySpec>();
		using (var before = world.Execute(handle))
		{
			before.MoveNext().Should().BeTrue();
			before.Current.Length.Should().Be(0);
		}

		using var commands = world.CreateCommandStream();
		var       created  = commands.CreateEntity();
		commands.Set(created, new Position { X = 42, Y = -7 });
		world.Playback(commands);

		using var after = world.Execute(handle);
		after.MoveNext().Should().BeTrue();
		after.Current.Length.Should().Be(1);
		world.Read<Position>(after.Current[0]).Should().Be(new Position { X = 42, Y = -7 });
	}


	[Fact]
	public void Execute_WhenCompiledQueryUsesAdded_ShouldTrackOnlyNewAdditionsAcrossExecutions()
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

		var first  = world.Spawn(new Position { X = 1, Y = 1 });
		var second = world.Spawn(new Velocity { X = 5, Y = 5 });

		var handle = world.Compile<AddedPositionQuerySpec>();
		using (var initial = world.Execute(handle))
		{
			initial.MoveNext().Should().BeTrue();
			initial.Current.Length.Should().Be(1);
			initial.Current[0].Should().Be(first);
		}

		using (var unchanged = world.Execute(handle))
		{
			unchanged.MoveNext().Should().BeTrue();
			unchanged.Current.Length.Should().Be(0);
		}

		world.Set(first, new Position { X  = 11, Y = 12 });
		world.Add(second, new Position { X = 20, Y = 21 });
		using var added = world.Execute(handle);
		added.MoveNext().Should().BeTrue();
		added.Current.Length.Should().Be(1);
		added.Current[0].Should().Be(second);
	}

	[Fact]
	public void Execute_WhenCompiledQueryUsesAllAndNone_ShouldReturnExpectedEntities()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 64,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 128,
				CommandPayloadCapacityPerType = 128,
				QueryResultCapacity           = 64
			}
		);

		using var commands = world.CreateCommandStream();
		var       first    = commands.CreateEntity();
		commands.Set(first, new Position { X = 1, Y = 1 });

		var second = commands.CreateEntity();
		commands.Set(second, new Position { X = 2, Y = 2 });
		commands.Set(second, new Velocity { X = 9, Y = 9 });
		world.Playback(commands);

		var       query  = world.Compile<PositionWithoutVelocityQuerySpec>();
		using var cursor = world.Execute(query);
		cursor.MoveNext().Should().BeTrue();
		cursor.Current.Length.Should().Be(1);
		cursor.Write<Position>(0).Should().Be(new Position { X = 1, Y = 1 });
	}


	[Fact]
	public void Execute_WhenCompiledQueryUsesAny_ShouldMatchEntitiesWithAtLeastOneType()
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
		commands.Set(first, new Position { X = 1, Y = 0 });

		var second = commands.CreateEntity();
		commands.Set(second, new Velocity { X = 1, Y = 0 });

		var third = commands.CreateEntity();
		commands.Set(third, new Position { X = 1, Y = 0 });
		commands.Set(third, new Velocity { X = 1, Y = 0 });

		var fourth = commands.CreateEntity();
		commands.Set(fourth, new ManagedTag { Payload = new("x") });
		world.Playback(commands);

		var       handle = world.Compile<AnyPositionOrVelocityQuerySpec>();
		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();
		cursor.Current.Length.Should().Be(3);
	}


	[Fact]
	public void Execute_WhenCompiledQueryUsesChanged_ShouldTrackChangesAcrossExecutions()
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

		var first = world.Spawn(new Position { X = 1, Y = 1 });
		_ = world.Spawn(new Position { X = 2, Y = 2 });
		_ = world.Spawn(new Velocity { X = 7, Y = 7 });

		var handle = world.Compile<ChangedPositionQuerySpec>();
		using (var initial = world.Execute(handle))
		{
			initial.MoveNext().Should().BeTrue();
			initial.Current.Length.Should().Be(2);
		}

		using (var unchanged = world.Execute(handle))
		{
			unchanged.MoveNext().Should().BeTrue();
			unchanged.Current.Length.Should().Be(0);
		}

		world.Set(first, new Position { X = 11, Y = 12 });
		using var changed = world.Execute(handle);
		changed.MoveNext().Should().BeTrue();
		changed.Current.Length.Should().Be(1);
		changed.Current[0].Should().Be(first);
	}

	[Fact]
	public void Set_WhenOverwritingExistingManagedComponent_ShouldReplaceReferenceAndTrackChangedOnce()
	{
		using var world = new World();
		var initialPayload = new Payload("initial");
		var entity = world.Spawn(new ManagedTag { Payload = initialPayload });
		var changedHandle = world.Compile<ChangedManagedTagQuerySpec>();

		using (var initial = world.Execute(changedHandle))
		{
			initial.MoveNext().Should().BeTrue();
			initial.Current.Length.Should().Be(1);
			initial.Current[0].Should().Be(entity);
		}

		using (var unchanged = world.Execute(changedHandle))
		{
			unchanged.MoveNext().Should().BeTrue();
			unchanged.Current.Length.Should().Be(0);
		}

		var replacementPayload = new Payload("replacement");
		world.Set(entity, new ManagedTag { Payload = replacementPayload });

		var resolved = world.Read<ManagedTag>(entity);
		resolved.Payload.Should().BeSameAs(replacementPayload);
		resolved.Payload!.Name.Should().Be("replacement");
		using (var changed = world.Execute(changedHandle))
		{
			changed.MoveNext().Should().BeTrue();
			changed.Current.Length.Should().Be(1);
			changed.Current[0].Should().Be(entity);
		}

		using var unchangedAgain = world.Execute(changedHandle);
		unchangedAgain.MoveNext().Should().BeTrue();
		unchangedAgain.Current.Length.Should().Be(0);
	}

	[Fact]
	public void ApplySetFromCommandKnownTransition_WhenOverwritingExistingComponent_ShouldReplaceValueAndTrackChangedOnce()
	{
		using var world = new World();
		var entity = world.Spawn(new Position { X = 1, Y = 2 });
		var changedHandle = world.Compile<ChangedPositionQuerySpec>();

		using (var initial = world.Execute(changedHandle))
		{
			initial.MoveNext().Should().BeTrue();
			initial.Current.Length.Should().Be(1);
			initial.Current[0].Should().Be(entity);
		}

		using (var unchanged = world.Execute(changedHandle))
		{
			unchanged.MoveNext().Should().BeTrue();
			unchanged.Current.Length.Should().Be(0);
		}

		int typeId = world.GetOrCreateComponentTypeId<Position>();
		world.DescribeSetTransition(entity, typeId, out int sourceArchetypeId, out int targetArchetypeId);
		sourceArchetypeId.Should().Be(targetArchetypeId);
		world.MatchesSetTransitionSource(entity, sourceArchetypeId, typeId, targetArchetypeId).Should().BeTrue();

		var replacement = new Position { X = 11, Y = 12 };
		world.ApplySetFromCommandKnownTransition(
			entity,
			in replacement,
			typeId,
			sourceArchetypeId,
			targetArchetypeId
		);

		world.Read<Position>(entity).Should().Be(replacement);
		using (var changed = world.Execute(changedHandle))
		{
			changed.MoveNext().Should().BeTrue();
			changed.Current.Length.Should().Be(1);
			changed.Current[0].Should().Be(entity);
		}

		using var unchangedAgain = world.Execute(changedHandle);
		unchangedAgain.MoveNext().Should().BeTrue();
		unchangedAgain.Current.Length.Should().Be(0);
	}


	[Fact]
	public void Execute_WhenCompiledQueryUsesOptional_ShouldMatchEntitiesWithAndWithoutOptionalType()
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

		var first  = world.Spawn(new Position { X = 1, Y = 1 });
		var second = world.Spawn(new Position { X = 2, Y = 2 }, new Velocity { X = 3, Y = 4 });
		_ = world.Spawn(new Velocity { X = 9, Y = 9 });

		var       handle = world.Compile<PositionWithOptionalVelocityQuerySpec>();
		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();
		cursor.Current.Length.Should().Be(2);
		var entities = cursor.Current.ToArray();
		entities.Should().Contain(first);
		entities.Should().Contain(second);
	}


	[Fact]
	public void Execute_WhenUsingMutableRefApis_ShouldTrackChangesForChangedFilter()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 16,
				ComponentTypeCapacity         = 32,
				CommandCapacity               = 32,
				CommandPayloadCapacityPerType = 32,
				QueryResultCapacity           = 16
			}
		);

		var entity         = world.Spawn(new Position { X = 1, Y = 1 });
		var changedHandle  = world.Compile<ChangedPositionQuerySpec>();
		var positionHandle = world.Compile<PositionQuerySpec>();

		using (var initial = world.Execute(changedHandle))
		{
			initial.MoveNext().Should().BeTrue();
			initial.Current.Length.Should().Be(1);
		}

		void AssertOnlyChangedEntity()
		{
			using var changed = world.Execute(changedHandle);
			changed.MoveNext().Should().BeTrue();
			changed.Current.Length.Should().Be(1);
			changed.Current[0].Should().Be(entity);
		}

		void ClearChangedWindow()
		{
			using var unchanged = world.Execute(changedHandle);
			unchanged.MoveNext().Should().BeTrue();
			unchanged.Current.Length.Should().Be(0);
		}

		ClearChangedWindow();
		ref var fromWorldGet = ref world.Write<Position>(entity);
		fromWorldGet.X += 1;
		AssertOnlyChangedEntity();

		ClearChangedWindow();
		var     accessor     = world.GetAccessor<Position>();
		ref var fromAccessor = ref accessor.Write(entity);
		fromAccessor.Y += 1;
		AssertOnlyChangedEntity();

		ClearChangedWindow();
		using (var cursor = world.Execute(positionHandle))
		{
			cursor.MoveNext().Should().BeTrue();
			cursor.Write<Position>(0).X += 1;
		}

		AssertOnlyChangedEntity();

		ClearChangedWindow();
		world.ForEach(positionHandle, (ref Position position) => position.X += 1);
		AssertOnlyChangedEntity();

		ClearChangedWindow();
		using (var cursor = world.Execute(positionHandle))
		{
			cursor.MoveNext().Should().BeTrue();
			cursor.ForEach((ref Position position) => position.Y += 1);
		}

		AssertOnlyChangedEntity();
	}

	[Fact]
	public void QueryView_Run_WhenCompiledQueryUsesChanged_ShouldOnlyExecuteChangedEntities()
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

		var first  = world.Spawn(new Position { X = 1, Y = 10 });
		var second = world.Spawn(new Position { X = 2, Y = 20 });
		var handle = world.Compile<ChangedPositionQuerySpec>();
		var query  = new QueryView<ChangedPositionQuerySpec>(world, handle);

		using (var initial = world.Execute(handle))
		{
			initial.MoveNext().Should().BeTrue();
			initial.Current.Length.Should().Be(2);
		}

		using (var unchanged = world.Execute(handle))
		{
			unchanged.MoveNext().Should().BeTrue();
			unchanged.Current.Length.Should().Be(0);
		}

		world.Set(first, new Position { X = 11, Y = 12 });

		var visited = new List<int>();
		query.Run<RecordingPositionJob, Position>(new(visited));

		visited.Should().Equal(11);
		world.Read<Position>(first).Y.Should().Be(112);
		world.Read<Position>(second).Y.Should().Be(20);
	}

	[Fact]
	public void QueryView_RunEntity_WhenCompiledQueryUsesAdded_ShouldOnlyExecuteAddedEntities()
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

		var first  = world.Spawn(new Position { X = 1, Y = 10 });
		var second = world.Spawn();
		var handle = world.Compile<AddedPositionQuerySpec>();
		var query  = new QueryView<AddedPositionQuerySpec>(world, handle);

		using (var initial = world.Execute(handle))
		{
			initial.MoveNext().Should().BeTrue();
			initial.Current.Length.Should().Be(1);
			initial.Current[0].Should().Be(first);
		}

		using (var unchanged = world.Execute(handle))
		{
			unchanged.MoveNext().Should().BeTrue();
			unchanged.Current.Length.Should().Be(0);
		}

		world.Add(second, new Position { X = 22, Y = 30 });

		var visited = new List<int>();
		query.RunEntity<RecordingEntityPositionJob, Position>(new(visited));

		visited.Should().Equal(22);
		world.Read<Position>(first).Y.Should().Be(10);
		world.Read<Position>(second).Y.Should().Be(130);
	}


	[Fact]
	public void ForEach_WhenCursorIsActive_ShouldAllowIndependentDirectIteration()
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
		commands.Set(entity, new Position { X = 1, Y = 1 });
		commands.Set(entity, new Velocity { X = 1, Y = 1 });
		world.Playback(commands);

		var       handle = world.Compile<PositionAndVelocityQuerySpec>();
		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();

		world.ForEach(
			handle, (ref Position position, in Velocity velocity) => { position.X += velocity.X; }
		);

		cursor.Write<Position>(0).X.Should().Be(2);
	}

	[Fact]
	public void Execute_WhenAnotherCursorIsActive_ShouldAllowIndependentEnumeration()
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
		commands.Set(entity, new Position { X = 1, Y = 1 });
		world.Playback(commands);

		var handle = world.Compile<PositionQuerySpec>();
		using var first = world.Execute(handle);
		first.MoveNext().Should().BeTrue();

		using var second = world.Execute(handle);
		second.MoveNext().Should().BeTrue();

		first.Current.Length.Should().Be(1);
		second.Current.Length.Should().Be(1);
		var firstEntity  = first.Current[0];
		var secondEntity = second.Current[0];
		firstEntity.Id.Should().BeGreaterThanOrEqualTo(0);
		secondEntity.Should().Be(firstEntity);
	}


	[Fact]
	public void ForEach_WhenUsingCompiledHandleDirectly_ShouldMutateWithoutCursorMaterialization()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 16,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 48,
				CommandPayloadCapacityPerType = 48,
				QueryResultCapacity           = 16
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 3; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y = i });
			commands.Set(entity, new Velocity { X = 5, Y = -1 });
		}

		world.Playback(commands);

		var handle = world.Compile<PositionAndVelocityQuerySpec>();
		world.ForEach(
			handle, (ref Position position, in Velocity velocity) =>
			{
				position.X += velocity.X;
				position.Y += velocity.Y;
			}
		);

		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();
		for (var i = 0; i < cursor.Current.Length; i++)
		{
			var updated = cursor.Write<Position>(i);
			updated.X.Should().Be(i + 5);
			updated.Y.Should().Be(i - 1);
		}
	}


	[Fact]
	public void QueryCursor_ForEach_WhenMutatingSequentially_ShouldUpdateComponentsInPlace()
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
		for (var i = 0; i < 3; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y  = i });
			commands.Set(entity, new Velocity { X = 10, Y = -2 });
		}

		world.Playback(commands);

		var       handle = world.Compile<PositionAndVelocityQuerySpec>();
		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();
		cursor.ForEach((ref Position position, in Velocity velocity) =>
			{
				position.X += velocity.X;
				position.Y += velocity.Y;
			}
		);

		for (var i = 0; i < cursor.Current.Length; i++)
		{
			var updated = cursor.Write<Position>(i);
			updated.X.Should().Be(i + 10);
			updated.Y.Should().Be(i - 2);
		}
	}


	[Fact]
	public void QueryCursor_ForEach3_WhenMutatingWithTwoReadonlyInputs_ShouldUpdateComponentsInPlace()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 16,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 48,
				CommandPayloadCapacityPerType = 48,
				QueryResultCapacity           = 16
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 3; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X     = i, Y = i });
			commands.Set(entity, new Velocity { X     = 2, Y = 3 });
			commands.Set(entity, new Acceleration { X = 1, Y = -1 });
		}

		world.Playback(commands);

		var       handle = world.Compile<PositionVelocityAccelerationQuerySpec>();
		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();
		cursor.ForEach((ref Position position, in Velocity velocity, in Acceleration acceleration) =>
			{
				position.X += velocity.X + acceleration.X;
				position.Y += velocity.Y + acceleration.Y;
			}
		);

		for (var i = 0; i < cursor.Current.Length; i++)
		{
			var updated = cursor.Write<Position>(i);
			updated.X.Should().Be(i + 3);
			updated.Y.Should().Be(i + 2);
		}
	}


	// ── 4e: 4-component ForEach ───────────────────────────────────────────

	[Fact]
	public void QueryCursor_ForEach4_WhenMutatingWithThreeReadonlyInputs_ShouldUpdateComponentsInPlace()
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

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 3; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X     = i, Y  = i });
			commands.Set(entity, new Velocity { X     = 1, Y  = 2 });
			commands.Set(entity, new Acceleration { X = 10, Y = 20 });
			commands.Set(entity, new Scale { Value    = 3 });
		}

		world.Playback(commands);

		var       handle = world.Compile<PositionVelocityAccelerationScaleQuerySpec>();
		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();
		cursor.ForEach((ref Position pos, in Velocity vel, in Acceleration acc, in Scale scale) =>
			{
				pos.X = vel.X + acc.X + scale.Value;
				pos.Y = vel.Y + acc.Y + scale.Value;
			}
		);

		for (var i = 0; i < cursor.Current.Length; i++)
		{
			var updated = cursor.Write<Position>(i);
			updated.X.Should().Be(14); // 1 + 10 + 3
			updated.Y.Should().Be(25); // 2 + 20 + 3
		}
	}


	[Fact]
	public void QueryCursor_Get_WhenResultSpansMultipleChunks_ShouldResolveEachEntityIndex()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 16,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 32,
				CommandPayloadCapacityPerType = 32,
				QueryResultCapacity           = 16,
				ChunkCapacity                 = 2
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 5; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y = i });
		}

		world.Playback(commands);

		var       handle = world.Compile<PositionQuerySpec>();
		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();
		cursor.Current.Length.Should().Be(5);

		var seen = new bool[5];
		for (var i = 0; i < cursor.Current.Length; i++)
		{
			var position = cursor.Write<Position>(i);
			var index    = (int)position.X;
			index.Should().BeInRange(0, 4);
			seen[index] = true;
		}

		seen.Should().OnlyContain(static value => value);
	}


	[Fact]
	public void QueryCursor_Get_WhenSwitchingTypesAndTraversingBackward_ShouldResolveCorrectComponents()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 32,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 96,
				CommandPayloadCapacityPerType = 96,
				QueryResultCapacity           = 32,
				ChunkCapacity                 = 2
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 3; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = 100 + i, Y = i });
			commands.Set(entity, new Velocity { X = 10 + i, Y  = -10 - i });
		}

		for (var i = 0; i < 3; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X     = 200 + i, Y = i });
			commands.Set(entity, new Velocity { X     = 20 + i, Y  = -20 - i });
			commands.Set(entity, new Acceleration { X = 1, Y       = 1 });
		}

		world.Playback(commands);

		var       handle = world.Compile<PositionAndVelocityQuerySpec>();
		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();
		cursor.Current.Length.Should().Be(6);

		int[] indexOrder = [0, 1, 2, 3, 2, 1, 4, 5, 4, 0];
		for (var i = 0; i < indexOrder.Length; i++)
		{
			int index  = indexOrder[i];
			var entity = cursor.Current[index];

			ref var velocity         = ref cursor.Write<Velocity>(index);
			var     expectedVelocity = world.Read<Velocity>(entity);
			velocity.X.Should().Be(expectedVelocity.X);
			velocity.Y.Should().Be(expectedVelocity.Y);

			ref var position         = ref cursor.Write<Position>(index);
			var     expectedPosition = world.Read<Position>(entity);
			position.X.Should().Be(expectedPosition.X);
			position.Y.Should().Be(expectedPosition.Y);
		}
	}


	[Fact]
	public void QueryCursor_Run_WhenUsingStructJob_ShouldMutateComponents()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 16,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 48,
				CommandPayloadCapacityPerType = 48,
				QueryResultCapacity           = 16
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 3; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y = i });
			commands.Set(entity, new Velocity { X = 2, Y = -1 });
		}

		world.Playback(commands);

		var       handle = world.Compile<PositionAndVelocityQuerySpec>();
		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();

		cursor.Run<IntegrateJob, Position, Velocity>(new(3f));

		for (var i = 0; i < cursor.Current.Length; i++)
		{
			var updated = cursor.Write<Position>(i);
			updated.X.Should().Be(i + 6);
			updated.Y.Should().Be(i - 3);
		}
	}

	[Fact]
	public void QueryCursor_WhenUsingTraversalFamilies_ShouldPreserveOrderAndMutationAcrossAdapters()
	{
		static (List<int> order, float[] yValues) ExecuteWith(
			Action<World, QueryHandle<PositionAndVelocityQuerySpec>, List<int>> run)
		{
			using var world = new World(
				new WorldConfig
				{
					EntityCapacity                = 16,
					ComponentTypeCapacity         = 16,
					CommandCapacity               = 48,
					CommandPayloadCapacityPerType = 48,
					QueryResultCapacity           = 16
				}
			);

			using var commands = world.CreateCommandStream();
			for (var i = 0; i < 5; i++)
			{
				var entity = commands.CreateEntity();
				commands.Set(entity, new Position { X = i, Y = i * 10 });
				commands.Set(entity, new Velocity { X = 0, Y = 3 });
			}

			world.Playback(commands);

			var handle = world.Compile<PositionAndVelocityQuerySpec>();
			var order  = new List<int>();
			run(world, handle, order);

			using var cursor = world.Execute(handle);
			cursor.MoveNext().Should().BeTrue();
			var yValues = new float[cursor.Current.Length];
			for (var i = 0; i < cursor.Current.Length; i++)
				yValues[i] = cursor.Write<Position>(i).Y;

			return (order, yValues);
		}

		var foreachResult = ExecuteWith(
			static (world, handle, order) =>
			{
				using var cursor = world.Execute(handle);
				cursor.MoveNext().Should().BeTrue();
				cursor.ForEach((ref Position position, in Velocity velocity) =>
					{
						order.Add((int)position.X);
						position.Y += velocity.Y;
					}
				);
			}
		);

		var foreachEntityResult = ExecuteWith(
			static (world, handle, order) =>
			{
				using var cursor = world.Execute(handle);
				cursor.MoveNext().Should().BeTrue();
				cursor.ForEachEntity((Entity entity, ref Position position, in Velocity velocity) =>
					{
						order.Add((int)position.X);
						position.Y += velocity.Y;
					}
				);
			}
		);

		var runResult = ExecuteWith(
			static (world, handle, order) =>
			{
				using var cursor = world.Execute(handle);
				cursor.MoveNext().Should().BeTrue();
				cursor.Run<RecordingIntegrateJob, Position, Velocity>(new(order));
			}
		);

		var runEntityResult = ExecuteWith(
			static (world, handle, order) =>
			{
				using var cursor = world.Execute(handle);
				cursor.MoveNext().Should().BeTrue();
				cursor.RunEntity<RecordingEntityIntegrateJob, Position, Velocity>(new(order));
			}
		);

		int[] expectedOrder = [0, 1, 2, 3, 4];
		float[] expectedYValues = [3, 13, 23, 33, 43];

		foreachResult.order.Should().Equal(expectedOrder);
		foreachEntityResult.order.Should().Equal(expectedOrder);
		runResult.order.Should().Equal(expectedOrder);
		runEntityResult.order.Should().Equal(expectedOrder);

		foreachResult.yValues.Should().Equal(expectedYValues);
		foreachEntityResult.yValues.Should().Equal(expectedYValues);
		runResult.yValues.Should().Equal(expectedYValues);
		runEntityResult.yValues.Should().Equal(expectedYValues);
	}


	[Fact]
	public void QueryView_Run_WhenWorldIsDisposedAndHandleBelongsToDifferentWorld_ShouldPreferDisposedValidation()
	{
		using var handleOwner = new World();
		var       handle      = handleOwner.Compile<PositionAndVelocityQuerySpec>();
		var       world       = new World();
		var       query       = new QueryView<PositionAndVelocityQuerySpec>(world, handle);
		world.Dispose();

		var action = () => query.Run<IntegrateJob, Position, Velocity>(new(1f));

		action.Should().Throw<ObjectDisposedException>();
	}

	[Fact]
	public void QueryView_RunParallel_WhenWorldIsDisposedAndHandleAndDegreeAreInvalid_ShouldPreferDisposedValidation()
	{
		using var handleOwner = new World();
		var       handle      = handleOwner.Compile<PositionAndVelocityQuerySpec>();
		var       world       = new World();
		var       query       = new QueryView<PositionAndVelocityQuerySpec>(world, handle);
		world.Dispose();

		var action = () => query.RunParallel<IntegrateJob, Position, Velocity>(new(1f), 0);

		action.Should().Throw<ObjectDisposedException>();
	}

	[Fact]
	public void QueryView_RunParallel_WhenHandleBelongsToDifferentWorldAndDegreeIsInvalid_ShouldPreferHandleValidation()
	{
		using var handleOwner = new World();
		using var world       = new World();
		var       entity      = world.Spawn(new Position { X = 1, Y = 2 }, new Velocity { X = 3, Y = 4 });
		var       handle      = handleOwner.Compile<PositionAndVelocityQuerySpec>();
		var       query       = new QueryView<PositionAndVelocityQuerySpec>(world, handle);

		var action = () => query.RunParallel<IntegrateJob, Position, Velocity>(new(1f), 0);

		action.Should().Throw<InvalidOperationException>();
		world.Read<Position>(entity).Should().Be(new Position { X = 1, Y = 2 });
	}

	[Fact]
	public void QueryView_Run_WhenUsingCompiledHandleAndStructJob_ShouldMutateComponents()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 16,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 48,
				CommandPayloadCapacityPerType = 48,
				QueryResultCapacity           = 16
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 3; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y = i });
			commands.Set(entity, new Velocity { X = 1, Y = 2 });
		}

		world.Playback(commands);

		var handle = world.Compile<PositionAndVelocityQuerySpec>();
		var query  = new QueryView<PositionAndVelocityQuerySpec>(world, handle);
		query.Run<IntegrateJob, Position, Velocity>(new(2f));

		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();
		for (var i = 0; i < cursor.Current.Length; i++)
		{
			var updated = cursor.Write<Position>(i);
			updated.X.Should().Be(i + 2);
			updated.Y.Should().Be(i + 4);
		}
	}

	[Fact]
	public void QueryView_RunEntity_WhenUsingCompiledHandleAndStructJob_ShouldMutateComponentsAndReceiveEntity()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 16,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 48,
				CommandPayloadCapacityPerType = 48,
				QueryResultCapacity           = 16
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 3; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y = i * 10 });
			commands.Set(entity, new Velocity { X = 0, Y = 3 });
		}

		world.Playback(commands);

		var handle = world.Compile<PositionAndVelocityQuerySpec>();
		var query  = new QueryView<PositionAndVelocityQuerySpec>(world, handle);
		var order  = new List<int>();
		query.RunEntity<RecordingEntityIntegrateJob, Position, Velocity>(new(order));

		order.Should().Equal(0, 1, 2);

		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();
		for (var i = 0; i < cursor.Current.Length; i++)
		{
			var updated = cursor.Write<Position>(i);
			updated.Y.Should().Be((i * 10) + 3);
		}
	}

	private sealed class CoordinatedQueryCompileProbe : IDisposable
	{
		public int BuildCount;
		public ManualResetEventSlim FirstBuildEntered { get; } = new(false);
		public ManualResetEventSlim ReleaseFirstBuild { get; } = new(false);
		public ManualResetEventSlim SecondBuildEntered { get; } = new(false);
		public ManualResetEventSlim SecondCompileStarting { get; } = new(false);

		public void Dispose()
		{
			FirstBuildEntered.Dispose();
			ReleaseFirstBuild.Dispose();
			SecondBuildEntered.Dispose();
			SecondCompileStarting.Dispose();
		}
	}

	private readonly struct CoordinatedPositionQuerySpec : ICompiledQuerySpec
	{
		public static CoordinatedQueryCompileProbe? Probe { get; set; }

		public void Build(ref QueryBuilder builder)
		{
			var probe       = Probe ?? throw new InvalidOperationException("Query compile probe is not configured.");
			var buildNumber = Interlocked.Increment(ref probe.BuildCount);
			if (buildNumber == 1)
			{
				probe.FirstBuildEntered.Set();
				probe.ReleaseFirstBuild.Wait();
			}
			else
				probe.SecondBuildEntered.Set();

			builder.All<Position>();
		}
	}
}

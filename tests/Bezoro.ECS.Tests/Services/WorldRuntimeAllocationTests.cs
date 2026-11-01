using System;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

public partial class WorldRuntimeTests
{
	[Fact]
	public void ForEach_WhenExecutingRepeatedHotPathAfterWarmup_ShouldNotAllocate()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 512,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 2048,
				CommandPayloadCapacityPerType = 2048,
				QueryResultCapacity           = 512
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 256; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y = i });
			commands.Set(entity, new Velocity { X = 1, Y = -1 });
		}

		world.Playback(commands);
		var handle = world.Compile<PositionAndVelocityQuerySpec>();
		QueryCursor.RefInAction<Position, Velocity> action = static (ref position, in velocity) =>
		{
			position.X += velocity.X;
			position.Y += velocity.Y;
		};

		// Warm up JIT/caches so the measurement captures steady-state allocations.
		world.ForEach(handle, action);

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		for (var iteration = 0; iteration < 200; iteration++)
			world.ForEach(handle, action);

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (var iteration = 0; iteration < 200; iteration++)
			world.ForEach(handle, action);

		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		allocated.Should().Be(0);
	}

	[Fact]
	public void GetAccessor_WhenExecutingRepeatedlyAfterWarmup_ShouldNotAllocate()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 512,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 2048,
				CommandPayloadCapacityPerType = 2048,
				QueryResultCapacity           = 512
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 256; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y = i });
		}

		world.Playback(commands);
		var handle   = world.Compile<PositionQuerySpec>();
		var entities = new Entity[256];
		using (var cursor = world.Execute(handle))
		{
			cursor.MoveNext().Should().BeTrue();
			cursor.Current.CopyTo(entities);
		}

		var accessor = world.GetAccessor<Position>();
		for (var i = 0; i < entities.Length; i++)
		{
			ref var position = ref accessor.Write(entities[i]);
			position.X += 1;
		}

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (var iteration = 0; iteration < 150; iteration++)
		{
			for (var i = 0; i < entities.Length; i++)
			{
				ref var position = ref accessor.Write(entities[i]);
				position.Y -= 1;
			}
		}

		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		allocated.Should().Be(0);
	}


	[Fact]
	public void Playback_WhenReusingCommandStreamForSetBurstAfterWarmup_ShouldNotAllocate()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 512,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 4096,
				CommandPayloadCapacityPerType = 4096,
				QueryResultCapacity           = 512
			}
		);

		using var create = world.CreateCommandStream();
		for (var i = 0; i < 256; i++)
		{
			var entity = create.CreateEntity();
			create.Set(entity, new Position { X = i, Y = i });
		}

		world.Playback(create);

		var handle   = world.Compile<PositionQuerySpec>();
		var entities = new Entity[256];
		using (var cursor = world.Execute(handle))
		{
			cursor.MoveNext().Should().BeTrue();
			cursor.Current.CopyTo(entities);
		}

		using var updates = world.CreateCommandStream();
		for (var i = 0; i < entities.Length; i++)
			updates.Set(entities[i], new Position { X = i + 1, Y = -i });

		world.Playback(updates);

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (var iteration = 0; iteration < 150; iteration++)
		{
			for (var i = 0; i < entities.Length; i++)
				updates.Set(entities[i], new Position { X = i + iteration, Y = -i - iteration });

			world.Playback(updates);
		}

		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		allocated.Should().Be(0);
	}

	[Fact]
	public void Set_WhenOverwritingExistingComponentsAfterWarmup_ShouldNotAllocate()
	{
		using var world = new World();
		var positionEntity = world.Spawn(new Position { X = 1, Y = 2 });
		var managedEntity = world.Spawn(new ManagedTag { Payload = new("initial") });
		var position = new Position { X = 11, Y = 12 };
		var payload = new Payload("replacement");
		var managedTag = new ManagedTag { Payload = payload };

		world.Set(positionEntity, in position);
		world.Set(managedEntity, in managedTag);

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		for (var iteration = 0; iteration < 200; iteration++)
		{
			world.Set(positionEntity, in position);
			world.Set(managedEntity, in managedTag);
		}

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (var iteration = 0; iteration < 200; iteration++)
		{
			world.Set(positionEntity, in position);
			world.Set(managedEntity, in managedTag);
		}

		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		allocated.Should().Be(0);
		world.Read<Position>(positionEntity).Should().Be(position);
		world.Read<ManagedTag>(managedEntity).Payload.Should().BeSameAs(payload);
	}


	[Fact]
	public void QueryCursor_ForEach_WhenExecutingRepeatedlyAfterWarmup_ShouldNotAllocate()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 512,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 2048,
				CommandPayloadCapacityPerType = 2048,
				QueryResultCapacity           = 512
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 256; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y = i });
			commands.Set(entity, new Velocity { X = 1, Y = -1 });
		}

		world.Playback(commands);
		var handle = world.Compile<PositionAndVelocityQuerySpec>();
		QueryCursor.RefInAction<Position, Velocity> action = static (ref position, in velocity) =>
		{
			position.X += velocity.X;
			position.Y += velocity.Y;
		};

		// Warm up JIT/caches so the measurement captures steady-state allocations.
		using (var warmup = world.Execute(handle))
		{
			warmup.MoveNext();
			warmup.ForEach(action);
		}

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		for (var iteration = 0; iteration < 200; iteration++)
		{
			using var cursor = world.Execute(handle);
			cursor.MoveNext();
			cursor.ForEach(action);
		}

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (var iteration = 0; iteration < 200; iteration++)
		{
			using var cursor = world.Execute(handle);
			cursor.MoveNext();
			cursor.ForEach(action);
		}

		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		allocated.Should().BeLessThanOrEqualTo(64);
	}

	[Fact]
	public void QueryCursor_ForEachEntity_WhenExecutingRepeatedlyAfterWarmup_ShouldNotAllocateForUnmanagedComponents()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 512,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 2048,
				CommandPayloadCapacityPerType = 2048,
				QueryResultCapacity           = 512
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 256; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y = i });
			commands.Set(entity, new Velocity { X = 1, Y = -1 });
		}

		world.Playback(commands);
		var handle = world.Compile<PositionAndVelocityQuerySpec>();

		using (var warmup = world.Execute(handle))
		{
			warmup.MoveNext();
			warmup.ForEachEntity(static (Entity entity, ref Position position, in Velocity velocity) =>
				{
					_          =  entity;
					position.X += velocity.X;
					position.Y += velocity.Y;
				}
			);
		}

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		for (var iteration = 0; iteration < 200; iteration++)
		{
			using var cursor = world.Execute(handle);
			cursor.MoveNext();
			cursor.ForEachEntity(static (Entity entity, ref Position position, in Velocity velocity) =>
				{
					_          =  entity;
					position.X += velocity.X;
					position.Y += velocity.Y;
				}
			);
		}

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (var iteration = 0; iteration < 200; iteration++)
		{
			using var cursor = world.Execute(handle);
			cursor.MoveNext();
			cursor.ForEachEntity(static (Entity entity, ref Position position, in Velocity velocity) =>
				{
					_          =  entity;
					position.X += velocity.X;
					position.Y += velocity.Y;
				}
			);
		}

		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		allocated.Should().BeLessThanOrEqualTo(64);
	}


	[Fact]
	public void QueryCursor_Get_WhenExecutingSequentiallyAfterWarmup_ShouldNotAllocate()
	{
		const int ENTITY_COUNT = 256;
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 512,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 2048,
				CommandPayloadCapacityPerType = 2048,
				QueryResultCapacity           = 512
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < ENTITY_COUNT; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y = -i });
		}

		world.Playback(commands);
		var handle = world.Compile<PositionQuerySpec>();

		// Warm up JIT/caches so the measurement captures steady-state allocations.
		using (var warmup = world.Execute(handle))
		{
			warmup.MoveNext();
			for (var i = 0; i < ENTITY_COUNT; i++)
			{
				ref var position = ref warmup.Write<Position>(i);
				position.X += 1f;
			}
		}

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		for (var iteration = 0; iteration < 200; iteration++)
		{
			using var cursor = world.Execute(handle);
			cursor.MoveNext();
			for (var i = 0; i < ENTITY_COUNT; i++)
			{
				ref var position = ref cursor.Write<Position>(i);
				position.X += 1f;
			}
		}

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (var iteration = 0; iteration < 200; iteration++)
		{
			using var cursor = world.Execute(handle);
			cursor.MoveNext();
			for (var i = 0; i < ENTITY_COUNT; i++)
			{
				ref var position = ref cursor.Write<Position>(i);
				position.X += 1f;
			}
		}

		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		allocated.Should().BeLessThanOrEqualTo(64);
	}

	[Fact]
	public void QueryView_ForEach_WhenExecutingRepeatedlyAfterWarmup_ShouldNotAllocateForUnmanagedComponents()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 512,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 2048,
				CommandPayloadCapacityPerType = 2048,
				QueryResultCapacity           = 512
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 256; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y = i });
			commands.Set(entity, new Velocity { X = 1, Y = -1 });
		}

		world.Playback(commands);
		var query = world.Query<PositionAndVelocityQuerySpec>();

		// Warm up JIT/caches so the measurement captures steady-state allocations.
		query.ForEach(static (Entity entity, ref Position position, in Velocity velocity) =>
			{
				_          =  entity;
				position.X += velocity.X;
				position.Y += velocity.Y;
			}
		);

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		for (var iteration = 0; iteration < 200; iteration++)
		{
			query.ForEach(static (Entity entity, ref Position position, in Velocity velocity) =>
				{
					_          =  entity;
					position.X += velocity.X;
					position.Y += velocity.Y;
				}
			);
		}

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (var iteration = 0; iteration < 200; iteration++)
		{
			query.ForEach(static (Entity entity, ref Position position, in Velocity velocity) =>
				{
					_          =  entity;
					position.X += velocity.X;
					position.Y += velocity.Y;
				}
			);
		}

		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		allocated.Should().BeLessThanOrEqualTo(64);
	}

	[Fact]
	public void QueryView_ForEachRead_WhenExecutingRepeatedlyAfterWarmup_ShouldNotAllocateForManagedComponents()
	{
		using var world = new World(new WorldConfig { EntityCapacity = 512, ComponentTypeCapacity = 16 });
		for (var i = 0; i < 256; i++)
			world.Spawn(new ManagedTag { Payload = new($"payload-{i}") });

		var query = world.Query<ManagedTagQuerySpec>();
		QueryView<ManagedTagQuerySpec>.EntityInAction<ManagedTag> action =
			static (Entity entity, in ManagedTag tag) => _ = tag.Payload;
		query.ForEachRead(action);

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		for (var iteration = 0; iteration < 200; iteration++)
			query.ForEachRead(action);

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (var iteration = 0; iteration < 200; iteration++)
			query.ForEachRead(action);

		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		allocated.Should().Be(0);
	}

	[Fact]
	public void QueryView_ForEach_WhenExecutingRepeatedlyAfterWarmup_ShouldNotAllocateForManagedComponents()
	{
		using var world = new World(new WorldConfig { EntityCapacity = 512, ComponentTypeCapacity = 16 });
		for (var i = 0; i < 256; i++)
			world.Spawn(new ManagedTag { Payload = new($"payload-{i}") });

		var query = world.Query<ManagedTagQuerySpec>();
		QueryView<ManagedTagQuerySpec>.EntityRefAction<ManagedTag> action =
			static (Entity entity, ref ManagedTag tag) => _ = tag.Payload;
		query.ForEach(action);

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		for (var iteration = 0; iteration < 200; iteration++)
			query.ForEach(action);

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (var iteration = 0; iteration < 200; iteration++)
			query.ForEach(action);

		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		allocated.Should().Be(0);
	}


	[Fact]
	public void QueryView_Run_WhenExecutingRepeatedHotPathAfterWarmup_ShouldNotAllocate()
	{
		using var world = new World(
			new WorldConfig
			{
				EntityCapacity                = 512,
				ComponentTypeCapacity         = 16,
				CommandCapacity               = 2048,
				CommandPayloadCapacityPerType = 2048,
				QueryResultCapacity           = 512
			}
		);

		using var commands = world.CreateCommandStream();
		for (var i = 0; i < 256; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y = i });
			commands.Set(entity, new Velocity { X = 1, Y = -1 });
		}

		world.Playback(commands);
		var handle = world.Compile<PositionAndVelocityQuerySpec>();
		var query  = new QueryView<PositionAndVelocityQuerySpec>(world, handle);

		// Warm up JIT/caches so the measurement captures steady-state allocations.
		query.Run<IntegrateJob, Position, Velocity>(new(0.5f));
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (var i = 0; i < 200; i++)
			query.Run<IntegrateJob, Position, Velocity>(new(0.5f));

		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		allocated.Should().Be(0);
	}
}

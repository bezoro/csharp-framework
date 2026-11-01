using System;
using System.Collections.Generic;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using JetBrains.Annotations;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

[TestSubject(typeof(World))]
public class WorldErgonomicApiTests
{
	[Fact]
	public void Query_WhenUsingGeneratedWithQuery_ShouldIterateEntitiesWithoutCursorCeremony()
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

		var first  = world.Spawn(new ErgonomicPosition { X = 1, Y = 2 });
		var second = world.Spawn(new ErgonomicPosition { X = 3, Y = 4 });
		_ = world.Spawn(new ErgonomicVelocity { X = 9, Y = 9 });

		var visited = new List<Entity>();

		world.Query<ErgonomicPositionQuery>().ForEach(entity => visited.Add(entity));

		visited.Should().Equal(first, second);
		world.Query<ErgonomicPositionQuery>().Count().Should().Be(2);
		world.Query<ErgonomicPositionQuery>().Any().Should().BeTrue();
	}

	[Fact]
	public void Query_WhenUsingTypedForEach_ShouldProvideEntityAndComponentsWithoutManualLookups()
	{
		using var world = new World();

		var entity = world.Spawn(
			new ErgonomicPosition { X = 1, Y = 2 },
			new ErgonomicVelocity { X = 3, Y = 4 }
		);

		Entity visitedEntity = default;
		world.Query<ErgonomicPositionVelocityQuery>().ForEach<ErgonomicPosition, ErgonomicVelocity>(
			(Entity entityId, ref ErgonomicPosition position, in ErgonomicVelocity velocity) =>
			{
				visitedEntity = entityId;
				position.X += velocity.X;
				position.Y += velocity.Y;
			}
		);

		visitedEntity.Should().Be(entity);
		world.Read<ErgonomicPosition>(entity).Should().Be(new ErgonomicPosition { X = 4, Y = 6 });
	}

	[Fact]
	public void Query_WhenUsingTypedForEachWhileCursorIsActive_ShouldAllowIndependentDirectIteration()
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

		var entity = world.Spawn(
			new ErgonomicPosition { X = 1, Y = 2 },
			new ErgonomicVelocity { X = 3, Y = 4 }
		);

		var handle = world.Compile<ErgonomicPositionVelocityQuery>();
		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();

		world.Query<ErgonomicPositionVelocityQuery>().ForEach<ErgonomicPosition, ErgonomicVelocity>(
			(Entity entityId, ref ErgonomicPosition position, in ErgonomicVelocity velocity) =>
			{
				entityId.Should().Be(entity);
				position.X += velocity.X;
				position.Y += velocity.Y;
			}
		);

		cursor.Write<ErgonomicPosition>(0).Should().Be(new ErgonomicPosition { X = 4, Y = 6 });
	}

	[Fact]
	public void Query_WhenUsingTypedReadOnlyForEachWithManagedComponent_ShouldProvideComponentWithoutManualLookups()
	{
		using var world = new World();

		var entity = world.Spawn(new ErgonomicManagedNote { Label = "pending", Count = 2 });

		Entity? observedEntity = null;
		string? observedLabel = null;
		world.Query<ErgonomicManagedNoteQuery>().ForEachRead<ErgonomicManagedNote>(
			(Entity entityId, in ErgonomicManagedNote note) =>
			{
				observedEntity = entityId;
				observedLabel  = note.Label;
			}
		);

		observedEntity.Should().Be(entity);
		observedLabel.Should().Be("pending");
	}

	[Fact]
	public void Query_WhenUsingManagedReadOnlyForEachAcrossChunks_ShouldProvideLiveEntityVersionsAndValues()
	{
		using var world = new World(new WorldConfig { ChunkCapacity = 1 });

		var first    = world.Spawn(new ErgonomicManagedNote { Label = "first", Count = 1 });
		var recycled = world.Spawn(new ErgonomicManagedNote { Label = "removed", Count = 2 });
		var third    = world.Spawn(new ErgonomicManagedNote { Label = "third", Count = 3 });
		world.Despawn(recycled);
		var replacement = world.Spawn(new ErgonomicManagedNote { Label = "replacement", Count = 4 });

		replacement.Id.Should().Be(recycled.Id);
		replacement.Version.Should().BeGreaterThan(recycled.Version);
		var visited = new Dictionary<Entity, (string Label, int Count)>();

		world.Query<ErgonomicManagedNoteQuery>().ForEachRead<ErgonomicManagedNote>(
			(Entity entity, in ErgonomicManagedNote note) => visited.Add(entity, (note.Label, note.Count))
		);

		visited.Should().BeEquivalentTo(
			new Dictionary<Entity, (string Label, int Count)>
			{
				[first]       = ("first", 1),
				[third]       = ("third", 3),
				[replacement] = ("replacement", 4)
			}
		);
		visited.Should().NotContainKey(recycled);
	}

	[Fact]
	public void Query_WhenUsingManagedMutableForEachAcrossChunks_ShouldMutateEveryValue()
	{
		using var world = new World(new WorldConfig { ChunkCapacity = 1 });

		var entities = new[]
		{
			world.Spawn(new ErgonomicManagedNote { Label = "first", Count = 1 }),
			world.Spawn(new ErgonomicManagedNote { Label = "second", Count = 2 }),
			world.Spawn(new ErgonomicManagedNote { Label = "third", Count = 3 })
		};

		world.Query<ErgonomicManagedNoteQuery>().ForEach<ErgonomicManagedNote>(
			(Entity _, ref ErgonomicManagedNote note) =>
			{
				note.Label += "!";
				note.Count *= 10;
			}
		);

		world.Read<ErgonomicManagedNote>(entities[0]).Should().Be(new ErgonomicManagedNote { Label = "first!", Count = 10 });
		world.Read<ErgonomicManagedNote>(entities[1]).Should().Be(new ErgonomicManagedNote { Label = "second!", Count = 20 });
		world.Read<ErgonomicManagedNote>(entities[2]).Should().Be(new ErgonomicManagedNote { Label = "third!", Count = 30 });
	}

	[Fact]
	public void Query_WhenManagedComponentIsMissingFromNonemptyMatch_ShouldThrowBeforeCallbackAndRemainUsable()
	{
		using var world = new World();
		world.Spawn(new ErgonomicManagedNote { Label = "pending", Count = 2 });
		var query         = world.Query<ErgonomicManagedNoteQuery>();
		var callbackCount = 0;

		Action mismatched = () => query.ForEachRead<ErgonomicReadOnlyNote>(
			(Entity _, in ErgonomicReadOnlyNote _) => callbackCount++
		);

		mismatched.Should().Throw<KeyNotFoundException>();
		callbackCount.Should().Be(0);

		query.ForEachRead<ErgonomicManagedNote>((Entity _, in ErgonomicManagedNote _) => callbackCount++);
		callbackCount.Should().Be(1);
	}

	[Fact]
	public void Query_WhenManagedComponentIsMissingFromEmptyMatch_ShouldNotRegisterTypeOrInvokeCallback()
	{
		using var world = new World();
		var query = world.Query<ErgonomicManagedNoteQuery>();
		var before = world.GetDiagnostics().ComponentTypeArena;
		var callbackCount = 0;

		query.ForEachRead<ErgonomicReadOnlyNote>((Entity _, in ErgonomicReadOnlyNote _) => callbackCount++);

		callbackCount.Should().Be(0);
		var after = world.GetDiagnostics().ComponentTypeArena;
		after.Used.Should().Be(before.Used);
		after.HighWatermark.Should().Be(before.HighWatermark);
	}

	[Fact]
	public void Query_WhenManagedCallbackIsNullAndWorldDisposed_ShouldValidateActionBeforeWorldState()
	{
		var world = new World();
		world.Spawn(new ErgonomicManagedNote { Label = "pending", Count = 2 });
		var query = world.Query<ErgonomicManagedNoteQuery>();
		world.Dispose();

		Action nullAction = () => query.ForEachRead<ErgonomicManagedNote>(null!);
		Action disposedAction = () => query.ForEachRead<ErgonomicManagedNote>(
			(Entity _, in ErgonomicManagedNote _) => { }
		);

		nullAction.Should().Throw<ArgumentNullException>().WithParameterName("action");
		disposedAction.Should().Throw<ObjectDisposedException>();
	}

	[Fact]
	public void Query_WhenUsingTypedReadOnlyForEachWithManagedComponent_ShouldNotTrackPotentialWrites()
	{
		using var world = new World();

		world.Spawn(new ErgonomicManagedNote { Label = "pending", Count = 2 });
		var changedHandle = world.Compile<ChangedErgonomicManagedNoteQuery>();
		using (var initial = world.Execute(changedHandle))
		{
			initial.MoveNext().Should().BeTrue();
			initial.Current.Length.Should().Be(1);
		}

		var visitCount = 0;
		world.Query<ErgonomicManagedNoteQuery>().ForEachRead<ErgonomicManagedNote>(
			(Entity _, in ErgonomicManagedNote _) => visitCount++
		);

		visitCount.Should().Be(1);
		using var changed = world.Execute(changedHandle);
		changed.MoveNext().Should().BeTrue();
		changed.Current.Length.Should().Be(0);
	}

	[Fact]
	public void Query_WhenUsingTypedReadOnlyForEachWithUnmanagedComponent_ShouldNotTrackPotentialWrites()
	{
		using var world = new World();

		world.Spawn(new ErgonomicPosition { X = 1, Y = 2 });
		var changedHandle = world.Compile<ChangedErgonomicPositionQuery>();
		using (var initial = world.Execute(changedHandle))
		{
			initial.MoveNext().Should().BeTrue();
			initial.Current.Length.Should().Be(1);
		}

		var visitCount = 0;
		world.Query<ErgonomicPositionQuery>().ForEachRead<ErgonomicPosition>(
			(Entity _, in ErgonomicPosition _) => visitCount++
		);

		visitCount.Should().Be(1);
		using var changed = world.Execute(changedHandle);
		changed.MoveNext().Should().BeTrue();
		changed.Current.Length.Should().Be(0);
	}

	[Fact]
	public void Query_WhenUsingTypedForEachWithManagedAndReadOnlyComponents_ShouldMutateWithoutManualLookups()
	{
		using var world = new World();

		var entity = world.Spawn(
			new ErgonomicManagedNote { Label = "pending", Count = 2 },
			new ErgonomicVelocity { X = 3, Y = 4 }
		);

		world.Query<ErgonomicManagedNoteVelocityQuery>().ForEach<ErgonomicManagedNote, ErgonomicVelocity>(
			(Entity entityId, ref ErgonomicManagedNote note, in ErgonomicVelocity velocity) =>
			{
				entityId.Should().Be(entity);
				note.Count += (int)(velocity.X + velocity.Y);
				note.Label += "!";
			}
		);

		world.Read<ErgonomicManagedNote>(entity).Should().Be(
			new ErgonomicManagedNote { Label = "pending!", Count = 9 }
		);
	}

	[Fact]
	public void Query_WhenManagedReadCallbackPlaysBackCommands_ShouldRejectUntilIterationCompletes()
	{
		using var world = new World();
		world.Spawn(new ErgonomicManagedNote { Label = "pending", Count = 2 });
		using var commands = world.CreateCommandStream();

		world.Query<ErgonomicManagedNoteQuery>().ForEachRead<ErgonomicManagedNote>(
			(Entity _, in ErgonomicManagedNote _) =>
			{
				var act = () => world.Playback(commands);

				act.Should().Throw<InvalidOperationException>()
				   .WithMessage("Playback cannot run while a query iteration is active.");
			}
		);

		world.Playback(commands);
	}

	[Fact]
	public void Query_WhenUnmanagedCallbackPlaysBackCommands_ShouldRejectUntilIterationCompletes()
	{
		using var world = new World();
		world.Spawn(new ErgonomicPosition { X = 1, Y = 2 });
		using var commands = world.CreateCommandStream();

		Action iterate = () => world.Query<ErgonomicPositionQuery>().ForEach<ErgonomicPosition>(
			(Entity _, ref ErgonomicPosition _) =>
			{
				var act = () => world.Playback(commands);

				act.Should().Throw<InvalidOperationException>()
				   .WithMessage("Playback cannot run while a query iteration is active.");
				throw new InvalidOperationException("Callback failed.");
			}
		);
		iterate.Should().Throw<InvalidOperationException>().WithMessage("Callback failed.");

		world.Playback(commands);
	}

	[Fact]
	public void Query_WhenUnmanagedCallbackResetsOrClearsWorld_ShouldRejectUntilIterationCompletes()
	{
		using var world = new World();
		world.Spawn(new ErgonomicPosition { X = 1, Y = 2 });

		world.Query<ErgonomicPositionQuery>().ForEach<ErgonomicPosition>(
			(Entity _, ref ErgonomicPosition _) =>
			{
				Action reset = world.Reset;
				Action clear = world.Clear;

				reset.Should().Throw<InvalidOperationException>()
				     .WithMessage("Reset cannot run while a query iteration is active.");
				clear.Should().Throw<InvalidOperationException>()
				     .WithMessage("Clear cannot run while a query iteration is active.");
			}
		);

		world.Reset();
		world.Clear();
	}

	[Fact]
	public void Query_WhenCachedAndWorldDisposed_ShouldThrowOnUnmanagedFastPath()
	{
		var world = new World();
		world.Spawn(new ErgonomicPosition { X = 1, Y = 2 });
		var query = world.Query<ErgonomicPositionQuery>();

		world.Dispose();

		Action act = () => query.ForEach<ErgonomicPosition>(
			(Entity entityId, ref ErgonomicPosition position) => position.X += 1
		);

		act.Should().Throw<ObjectDisposedException>();
	}

	[Fact]
	public void ResourceApis_WhenUsingExplicitReadWriteMethods_ShouldAvoidExceptionDrivenFlow()
	{
		using var world = new World();

		world.HasResource<ErgonomicSettings>().Should().BeFalse();
		ErgonomicSettings existing;
		world.TryReadResource(out existing).Should().BeFalse();
		existing.Should().BeNull();

		ref var created = ref world.GetOrCreateResource<ErgonomicSettings>();
		created.Gravity = 9.81f;

		world.HasResource<ErgonomicSettings>().Should().BeTrue();
		world.TryReadResource(out existing).Should().BeTrue();
		existing.Should().NotBeNull();
		existing.Gravity.Should().Be(9.81f);
		world.ReadResource<ErgonomicSettings>().Gravity.Should().Be(9.81f);

		ref var writable = ref world.WriteResource<ErgonomicSettings>();
		writable.Gravity = 12.5f;

		world.ReadResource<ErgonomicSettings>().Gravity.Should().Be(12.5f);
		world.RemoveResource<ErgonomicSettings>().Should().BeTrue();
		world.HasResource<ErgonomicSettings>().Should().BeFalse();
	}

	[Fact]
	public void ComponentApis_WhenUsingTryWrite_ShouldMutateWithoutCopyWriteback()
	{
		using var world = new World();
		var       entity = world.Spawn(new ErgonomicPosition { X = 1, Y = 2 });

		world.TryWrite<ErgonomicPosition>(entity, out var position).Should().BeTrue();
		position.Value.X += 10;
		position.Value.Y += 20;

		world.Read<ErgonomicPosition>(entity).Should().Be(new ErgonomicPosition { X = 11, Y = 22 });
	}

}

[Query]
[With(typeof(ErgonomicPosition))]
internal readonly partial struct ErgonomicPositionQuery;

[Query]
[With(typeof(ErgonomicPosition))]
[With(typeof(ErgonomicVelocity))]
internal readonly partial struct ErgonomicPositionVelocityQuery;

[Query]
[With(typeof(ErgonomicManagedNote))]
internal readonly partial struct ErgonomicManagedNoteQuery;

[Query]
[With(typeof(ErgonomicManagedNote))]
[With(typeof(ErgonomicVelocity))]
internal readonly partial struct ErgonomicManagedNoteVelocityQuery;

internal struct ErgonomicManagedNote
{
	public string Label;
	public int    Count;
}

internal struct ErgonomicPosition
{
	public float X;
	public float Y;
}

internal sealed class ErgonomicSettings
{
	public float Gravity { get; set; }
}

internal struct ErgonomicVelocity
{
	public float X;
	public float Y;
}

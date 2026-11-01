using System;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

public sealed class WorldRelationCapacityTests
{
	[Fact]
	public void RelationTargetChurn_ExhaustsReservedTypesBeforeChangingTheNewRelationship()
	{
		using var world = new World(new WorldConfig
		{
			EntityCapacity = 4, QueryResultCapacity = 4, ComponentTypeCapacity = 2,
			CommandCapacity = 4, CommandPayloadCapacityPerType = 4
		});
		var source = world.Spawn();
		for (var generation = 0; generation < 2; generation++)
		{
			var target = world.Spawn();
			world.AddRelation<Link>(source, target);
			world.Despawn(target);
		}
		var newTarget = world.Spawn();
		Action add = () => world.AddRelation<Link>(source, newTarget);
		add.Should().Throw<InvalidOperationException>().WithMessage("*type capacity*");
		world.EntityCount.Should().Be(2);
		world.HasRelation<Link>(source, newTarget).Should().BeFalse();
		world.HasRelation<Link>(source, Entity.Wildcard).Should().BeFalse();
		world.GetDiagnostics().ComponentTypeArena.Used.Should().Be(2);

		world.Reset();
		var afterResetSource = world.Spawn();
		var afterResetTarget = world.Spawn();
		Action afterReset = () => world.AddRelation<Link>(afterResetSource, afterResetTarget);
		afterReset.Should().Throw<InvalidOperationException>().WithMessage("*type capacity*");
		world.HasRelation<Link>(afterResetSource, afterResetTarget).Should().BeFalse();
	}

	private struct Link;
}

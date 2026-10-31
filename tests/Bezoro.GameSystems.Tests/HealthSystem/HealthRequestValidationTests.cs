using System;
using Bezoro.Core.Types;
using Bezoro.ECS.Services;
using Bezoro.GameSystems.HealthSystem.Extensions;
using Bezoro.GameSystems.HealthSystem.Types;
using Xunit;

namespace Bezoro.GameSystems.Tests.HealthSystem;

public sealed class HealthRequestValidationTests
{
	[Fact]
	public void InvalidMaxUpdateMode_DoesNotPartiallyChangeHealth()
	{
		using var world = new World();
		world.AddSystem(new Bezoro.GameSystems.HealthSystem.Services.HealthSystem());
		var entity = world.Spawn(new Health(100u, 50u));
		Assert.Throws<ArgumentOutOfRangeException>(() =>
		{
			world.QueueSetHealthMax(entity, 200u, (MaxValueUpdateMode)99);
			world.Tick(0f);
		});
		Assert.Equal(100u, world.Read<Health>(entity).Max);
		Assert.Equal(50u, world.Read<Health>(entity).Current);
		Assert.Equal(0, world.ReadResource<HealthEventsResource>().Count);
	}
}

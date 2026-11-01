using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

public sealed class WorldSchedulerIntervalTests
{
	[Fact]
	public void ExtremeFiniteDeltaTime_ExhaustsCatchUpWithZeroDeltaTicks()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 1 });
		var system = new LargeIntervalSystem();
		world.AddSystem(system);
		world.Tick(float.MaxValue);
		world.Tick(float.MaxValue);
		Assert.Equal(2, system.Count);

		for (int tick = 0; tick < 5; tick++) world.Tick(0f);

		Assert.Equal(4, system.Count);
		Assert.Equal(float.MaxValue / 2f, system.LastDeltaTime);
	}

	[NoWorldAccess]
	private sealed class LargeIntervalSystem : ISystem
	{
		public int Count { get; private set; }
		public float LastDeltaTime { get; private set; }
		public SystemUpdateSettings UpdateSettings => SystemUpdateSettings.FixedInterval(float.MaxValue / 2f);
		public void Update(in SystemContext context)
		{
			Count++;
			LastDeltaTime = context.DeltaTime;
		}
	}
}

using System;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

public sealed class WorldSchedulerAllocationTests
{
	[Fact]
	public void DisabledSystems_DoNotAllocateExecutionStorageOnTheirFirstTick()
	{
		// Initialize scheduler code with another owner, keeping this world's first pass measurable.
		using (var warmup = CreateDisabledWorld()) warmup.Tick(0.125f);
		using var world = CreateDisabledWorld();
		world.GetScheduleDiagnostics();

		long before = GC.GetAllocatedBytesForCurrentThread();
		world.Tick(0.125f);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		Assert.Equal(0, allocated);
	}

	[Theory]
	[InlineData(1, true)]
	[InlineData(8, true)]
	[InlineData(8, false)]
	public void SystemsWithoutCommands_DoNotAllocateAfterWarmup(int systemCount, bool enabled)
	{
		const int WARMUP_TICKS = 20_000;
		const int MEASURED_TICKS = 2_000;
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 1 });
		var systems = new EmptySystem[systemCount];
		for (int index = 0; index < systemCount; index++)
		{
			systems[index] = new EmptySystem();
			world.AddSystem(systems[index]);
		}
		world.SetSystemSetEnabled<OptionalSystems>(enabled);
		for (int tick = 0; tick < WARMUP_TICKS; tick++) world.Tick(0.125f);

		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int tick = 0; tick < MEASURED_TICKS; tick++) world.Tick(0.125f);
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		Assert.Equal(0, allocated);
		foreach (var system in systems)
		{
			Assert.Equal(enabled ? WARMUP_TICKS + MEASURED_TICKS : 0, system.Count);
			Assert.Equal(enabled ? (WARMUP_TICKS + MEASURED_TICKS) * 0.125f : 0f, system.Elapsed);
		}
	}

	[Fact]
	public void UnusedCommandStreams_AllocateOnlyStreamObjects()
	{
		using var world = new World();
		using (world.CreateCommandStream()) { }
		var streams = new CommandStream[32];
		try
		{
			long before = GC.GetAllocatedBytesForCurrentThread();
			for (int index = 0; index < streams.Length; index++)
				streams[index] = world.CreateCommandStream();
			long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

			Assert.InRange(allocated, 1, streams.Length * 256);
			foreach (var stream in streams)
			{
				Assert.False(stream.HasCommands);
				Assert.Equal(0, stream.GetDiagnostics().RecordedCommands);
				stream.Reset();
				world.Playback(stream);
			}
		}
		finally
		{
			foreach (var stream in streams) stream?.Dispose();
		}
	}

	private sealed class OptionalSystems;

	private static World CreateDisabledWorld()
	{
		var world = new World(new WorldConfig { MaxDegreeOfParallelism = 1 });
		for (int index = 0; index < 8; index++) world.AddSystem(new EmptySystem());
		world.SetSystemSetEnabled<OptionalSystems>(false);
		return world;
	}

	[NoWorldAccess, SystemSet(typeof(OptionalSystems))]
	private sealed class EmptySystem : ISystem
	{
		public int Count { get; private set; }
		public float Elapsed { get; private set; }
		public void Update(in SystemContext context)
		{
			Elapsed += context.DeltaTime;
			Count++;
		}
	}
}

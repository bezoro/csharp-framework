using System;
using System.Linq;
using System.Threading.Tasks;
using Bezoro.ECS.Internal;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using Xunit;

namespace Bezoro.ECS.Tests.Internal;

public sealed class WorldConcurrentRegistrationTests
{
	[Fact]
	public void ParallelCommandRecording_FirstUseOfDifferentTypesGetsDistinctStableIds()
	{
		using var world = new World(new WorldConfig
		{
			EntityCapacity = 8, ComponentTypeCapacity = 4, QueryResultCapacity = 8,
			CommandCapacity = 16, CommandPayloadCapacityPerType = 4, ChunkCapacity = 2
		});
		var entities = Enumerable.Range(0, 4).Select(_ => world.Spawn()).ToArray();
		var streams = Enumerable.Range(0, 4).Select(_ => world.CreateCommandStream()).ToArray();
		try
		{
			ParallelWorkScheduler.Execute(4, 4, index =>
			{
				switch (index)
				{
					case 0: streams[index].Set(entities[index], 1); break;
					case 1: streams[index].Set(entities[index], 2L); break;
					case 2: streams[index].Set(entities[index], 3f); break;
					case 3: streams[index].Set(entities[index], 4d); break;
				}
			});
			foreach (var stream in streams) world.Playback(stream);
			world.Read<int>(entities[0]).Should().Be(1);
			world.Read<long>(entities[1]).Should().Be(2);
			world.Read<float>(entities[2]).Should().Be(3);
			world.Read<double>(entities[3]).Should().Be(4);
			world.GetDiagnostics().ComponentTypeArena.Used.Should().Be(4);
		}
		finally { foreach (var stream in streams) stream.Dispose(); }
	}

	[Fact]
	public void ChangeVersions_RemainUniqueUnderConcurrentWritersAndPassThe32BitBoundary()
	{
		using var world = new World(new WorldConfig { EntityCapacity = 1, QueryResultCapacity = 1 });
		var store = new WorldEntityStore(world, new WorldConfig { EntityCapacity = 1 }, new WorldRelationIndex());
		try
		{
			var tracker = new WorldChangeTracker(store, new WorldConfig());
			typeof(WorldChangeTracker).GetField("_componentChangeVersion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
				.SetValue(tracker, (ulong)uint.MaxValue - 1);
			var versions = new ulong[10_000];
			Parallel.For(0, versions.Length, index =>
			{
				versions[index] = tracker.AdvanceComponentChangeVersion();
				tracker.MarkComponentChanged(index, 0, versions[index]);
			});
			versions.Distinct().Should().HaveCount(versions.Length);
			tracker.CurrentChangeVersion.Should().Be((ulong)uint.MaxValue - 1 + (ulong)versions.Length);
		}
		finally { store.Dispose(); }
	}
}

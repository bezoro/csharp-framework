using System;
using System.Linq;
using System.Threading;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

public sealed class CommandStreamConcurrencyTests
{
	[Fact]
	public void ConcurrentProducers_PreserveTemporaryIdentitiesAndTheirPayloads()
	{
		const int count = 2_048;
		using var world = CreateWorld(count);
		using var stream = world.CreateCommandStream();
		var temporaryEntities = new Entity[count];
		RunProducers(count, index => temporaryEntities[index] = stream.CreateEntity(new RecordedValue { Value = index }));

		Assert.Equal(count, temporaryEntities.Distinct().Count());
		Assert.Equal(count, stream.GetDiagnostics().RecordedCommands);
		world.Playback(stream);
		Assert.Equal(count, world.EntityCount);
		using var cursor = world.Execute(world.Compile<RecordedValues>());
		Assert.True(cursor.MoveNext());
		var seen = new bool[count];
		for (int index = 0; index < cursor.Count; index++)
		{
			int value = cursor.Read<RecordedValue>(index).Value;
			Assert.InRange(value, 0, count - 1);
			Assert.False(seen[value]);
			seen[value] = true;
		}
		Assert.All(seen, Assert.True);
	}

	[Fact]
	public void ConcurrentProducers_KeepManagedPayloadsWithTheCorrectEntity()
	{
		const int count = 1_024;
		using var world = CreateWorld(count);
		using var stream = world.CreateCommandStream();
		var entities = Enumerable.Range(0, count).Select(_ => world.Spawn()).ToArray();
		RunProducers(count, index => stream.Set(entities[index], new ManagedValue { Text = index.ToString() }));
		Assert.Equal(count, stream.GetDiagnostics().RecordedCommands);
		world.Playback(stream);
		for (int index = 0; index < count; index++)
			Assert.Equal(index.ToString(), world.Read<ManagedValue>(entities[index]).Text);
	}

	[Fact]
	public void ConcurrentProducers_RespectTheCommandCapacityAndCountEveryDroppedCommand()
	{
		const int count = 1_024;
		const int capacity = 128;
		using var world = CreateWorld(count, capacity, WorldOverflowPolicy.DropNewest);
		using var stream = world.CreateCommandStream();
		var entities = Enumerable.Range(0, count).Select(_ => world.Spawn()).ToArray();
		RunProducers(count, index => stream.Set(entities[index], new RecordedValue { Value = index }));
		Assert.Equal(capacity, stream.GetDiagnostics().RecordedCommands);
		Assert.Equal(count - capacity, stream.GetDiagnostics().OverflowCount);
		world.Playback(stream);
		Assert.Equal(capacity, entities.Count(world.Has<RecordedValue>));
	}

	private static void RunProducers(int count, Action<int> record)
	{
		const int workerCount = 4;
		using var start = new Barrier(workerCount);
		var workers = new Thread[workerCount];
		var errors = new Exception?[workerCount];
		for (int worker = 0; worker < workerCount; worker++)
		{
			int workerIndex = worker;
			workers[worker] = new Thread(() =>
			{
				try
				{
					if (!start.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
					for (int index = workerIndex; index < count; index += workerCount) record(index);
				}
				catch (Exception error) { errors[workerIndex] = error; }
			}) { IsBackground = true };
			workers[worker].Start();
		}
		foreach (var worker in workers) Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
		Assert.All(errors, Assert.Null);
	}

	private static World CreateWorld(int count, int? commandCapacity = null, WorldOverflowPolicy overflowPolicy = WorldOverflowPolicy.FailFast) =>
		new(new WorldConfig
		{
			EntityCapacity = count, ComponentTypeCapacity = 4, QueryResultCapacity = count,
			CommandCapacity = commandCapacity ?? count, CommandPayloadCapacityPerType = count,
			ChunkCapacity = 64, OverflowPolicy = overflowPolicy
		});

	private struct RecordedValue { public int Value; }
	private struct ManagedValue { public string Text; }
	private readonly struct RecordedValues : Bezoro.ECS.Abstractions.ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder) => builder.With<RecordedValue>();
	}
}

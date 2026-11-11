using System;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

public sealed class QueryCursorConcurrencyTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ConcurrentCopyDisposal_CannotReleaseBuffersDuringAnActiveCallback(bool useJob)
	{
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		using var world = new World(new WorldConfig
		{
			EntityCapacity = 4, QueryResultCapacity = 4, ComponentTypeCapacity = 2, ChunkCapacity = 1
		});
		world.Spawn(new Position());
		var cursor = world.Execute(world.Compile<Positions>());
		Assert.True(cursor.MoveNext());
		var copy = cursor;
		Task running = Task.Run(() =>
		{
			void Visit(ref Position position)
			{
				entered.Set();
				if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
				position.Value++;
			}
			if (useJob) cursor.Run<BlockingJob, Position>(new(entered, release));
			else cursor.ForEach<Position>(Visit);
		});
		try
		{
			Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
			Assert.Throws<InvalidOperationException>(() => copy.Dispose());
			Assert.Equal(1, copy.Count);
		}
		finally { release.Set(); }
		await running.WaitAsync(TimeSpan.FromSeconds(10));
		copy.Dispose();
		Assert.Throws<ObjectDisposedException>(() => cursor.ForEach<Position>((ref Position _) => { }));
		world.Reset();
	}

	private struct Position { public int Value; }
	private readonly struct BlockingJob(ManualResetEventSlim entered, ManualResetEventSlim release) : IForEach<Position>
	{
		public void Execute(ref Position position)
		{
			entered.Set();
			if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
			position.Value++;
		}
	}
	private readonly struct Positions : ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder) => builder.With<Position>();
	}
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

public sealed class WorldSystemContextLifetimeTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void CapturedContext_ExpiresAfterUpdateAndCannotAcquireTheNextStream(bool acquireStream)
	{
		using var world = CreateWorld();
		SystemContext captured = default;
		CommandStream? previousStream = null;
		int calls = 0;
		world.AddSystem(new CallbackSystem(context =>
		{
			if (calls++ > 0) AssertExpired(captured);
			captured = context;
			Assert.Same(world, context.World);
			Assert.Equal(0.125f, context.DeltaTime);
			Assert.Equal(Stage.Tick, context.Stage);
			if (!acquireStream) return;
			var stream = context.CommandStream;
			Assert.Same(stream, context.CommandStream);
			if (previousStream is not null)
			{
				Assert.NotSame(previousStream, stream);
				Assert.Throws<ObjectDisposedException>(() => previousStream.CreateEntity());
			}
			stream.CreateEntity();
			previousStream = stream;
		}));

		world.Tick(0.125f);
		AssertExpired(captured);
		world.Tick(0.125f);
		AssertExpired(captured);
		Assert.Equal(acquireStream ? 2 : 0, world.EntityCount);
		if (previousStream is not null)
			Assert.Throws<ObjectDisposedException>(() => previousStream.CreateEntity());
	}

	[Fact]
	public void FailingUpdate_ExpiresItsContextEvenWithoutCommandAccess()
	{
		using var world = CreateWorld();
		SystemContext captured = default;
		world.AddSystem(new CallbackSystem(context =>
		{
			captured = context;
			throw new InvalidOperationException("Update failed.");
		}));

		Assert.Throws<InvalidOperationException>(() => world.Tick(0.125f));
		AssertExpired(captured);
		Assert.Equal(0, world.EntityCount);
	}

	[Fact]
	public void PublicConstructor_PreservesExplicitWorldAndStreamOwnership()
	{
		using var world = CreateWorld();
		using var foreign = CreateWorld();
		using var stream = world.CreateCommandStream();
		var context = new SystemContext(0.125f, Stage.Input, world, stream);
		Assert.Same(world, context.World);
		Assert.Same(stream, context.CommandStream);
		Assert.Equal(0.125f, context.DeltaTime);
		Assert.Equal(Stage.Input, context.Stage);
		Assert.Throws<ArgumentException>(() => new SystemContext(0f, Stage.Tick, foreign, stream));
		Assert.Throws<ArgumentNullException>(() => new SystemContext(0f, Stage.Tick, null!, stream));
		Assert.Throws<ArgumentNullException>(() => new SystemContext(0f, Stage.Tick, world, null!));
	}

	[Fact]
	public void RunConditionFailure_ClearsPartiallySelectedBatchBeforeTheNextUpdate()
	{
		using var world = CreateWorld();
		int firstCalls = 0;
		int conditionalCalls = 0;
		world.AddSystem(new CallbackSystem(context => { firstCalls++; context.CommandStream.CreateEntity(); }));
		world.AddSystem(new ConditionalSystem(_ => conditionalCalls++));
		var condition = new NestedCondition { Throw = true };
		world.SetSystemSetRunCondition<ConditionalSystems>(condition);

		Assert.Throws<InvalidOperationException>(() => world.Tick(0.125f));
		Assert.Equal(0, firstCalls);
		Assert.Equal(0, world.EntityCount);
		condition.Throw = false;
		world.Tick(0.125f);
		Assert.Equal(1, firstCalls);
		Assert.Equal(1, conditionalCalls);
		Assert.Equal(1, world.EntityCount);
	}

	[Fact]
	public void NestedWorldUpdates_FromConditionsUpdatesAndShutdownUseIndependentBatches()
	{
		using var inner = CreateWorld(2);
		using var outer = CreateWorld(2);
		inner.AddSystem(new CallbackSystem(context => context.CommandStream.CreateEntity()));
		outer.AddSystem(new CallbackSystem(context => context.CommandStream.CreateEntity(), _ => inner.Tick(0.125f)));
		outer.AddSystem(new ConditionalSystem(context =>
		{
			var stream = context.CommandStream;
			stream.CreateEntity();
			inner.Tick(0.125f);
			Assert.Same(stream, context.CommandStream);
			stream.CreateEntity();
		}));
		outer.SetSystemSetRunCondition<ConditionalSystems>(new NestedCondition { Nested = inner });

		outer.Tick(0.125f);
		Assert.Equal(3, outer.EntityCount);
		Assert.Equal(2, inner.EntityCount);
		outer.Dispose();
		Assert.Equal(3, inner.EntityCount);
	}

	[Fact]
	public void ParallelBatch_FlushesInPlanOrderWhenStreamsAreCreatedInReverseOrder()
	{
		using var secondRecorded = new ManualResetEventSlim();
		using var world = CreateWorld(2);
		var entity = world.Spawn(new CommandValue());
		world.AddSystem(new CallbackSystem(context =>
		{
			if (!secondRecorded.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
			context.CommandStream.Set(entity, new CommandValue { Value = 1 });
		}));
		world.AddSystem(new CallbackSystem(context =>
		{
			context.CommandStream.Set(entity, new CommandValue { Value = 2 });
			secondRecorded.Set();
		}));

		world.Tick(0.125f);
		Assert.Equal(2, world.Read<CommandValue>(entity).Value);
	}

	[Fact]
	public void ParallelCommandAccess_ObtainsOneCanonicalStreamForTheExecution()
	{
		using var world = CreateWorld();
		var streams = new CommandStream[32];
		world.AddSystem(new CallbackSystem(context =>
		{
			Parallel.For(0, streams.Length, index => streams[index] = context.CommandStream);
			foreach (var stream in streams) Assert.Same(context.CommandStream, stream);
			context.CommandStream.CreateEntity();
		}));
		world.Tick(0.125f);
		Assert.Equal(1, world.EntityCount);
		foreach (var stream in streams) Assert.Throws<ObjectDisposedException>(() => stream.CreateEntity());
	}

	[Fact]
	public async Task FailingParallelBatch_JoinsWorkersBeforeDisposingTheirStreams()
	{
		using var entered = new CountdownEvent(2);
		using var failNow = new ManualResetEventSlim();
		using var failed = new ManualResetEventSlim();
		using var releaseOther = new ManualResetEventSlim();
		using var world = CreateWorld(2);
		CommandStream? first = null;
		CommandStream? second = null;
		SystemContext captured = default;
		world.AddSystem(new CallbackSystem(context =>
		{
			captured = context;
			first = context.CommandStream;
			first.CreateEntity();
			entered.Signal();
			if (!failNow.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
			failed.Set();
			throw new InvalidOperationException("Worker failed.");
		}));
		world.AddSystem(new CallbackSystem(context =>
		{
			second = context.CommandStream;
			second.CreateEntity();
			entered.Signal();
			if (!releaseOther.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
			Assert.True(first!.HasCommands);
		}));
		Task running = Task.Run(() => world.Tick(0.125f));
		try
		{
			Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
			failNow.Set();
			Assert.True(failed.Wait(TimeSpan.FromSeconds(10)));
			Assert.False(running.IsCompleted);
			Assert.True(first!.HasCommands);
		}
		finally { failNow.Set(); releaseOther.Set(); }
		await Assert.ThrowsAsync<InvalidOperationException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
		AssertExpired(captured);
		Assert.Throws<ObjectDisposedException>(() => first!.CreateEntity());
		Assert.Throws<ObjectDisposedException>(() => second!.CreateEntity());
		Assert.Equal(0, world.EntityCount);
	}

	private static void AssertExpired(SystemContext context)
	{
		Assert.Throws<ObjectDisposedException>(() => context.CommandStream);
		Assert.Throws<ObjectDisposedException>(() => context.World);
		Assert.Throws<ObjectDisposedException>(() => context.Stage);
		Assert.Throws<ObjectDisposedException>(() => context.DeltaTime);
	}

	private static World CreateWorld(int maxDegreeOfParallelism = 1) => new(new WorldConfig
	{
		EntityCapacity = 32, ComponentTypeCapacity = 4, QueryResultCapacity = 32,
		CommandCapacity = 32, CommandPayloadCapacityPerType = 32, ChunkCapacity = 4,
		MaxDegreeOfParallelism = maxDegreeOfParallelism
	});

	private sealed class ConditionalSystems;
	private struct CommandValue { public int Value; }

	[NoWorldAccess]
	private sealed class CallbackSystem(Action<SystemContext> update, Action<World>? destroy = null) : ISystem
	{
		public void Update(in SystemContext context) => update(context);
		public void OnDestroy(World world) => destroy?.Invoke(world);
	}

	[NoWorldAccess, SystemSet(typeof(ConditionalSystems))]
	private sealed class ConditionalSystem(Action<SystemContext> update) : ISystem
	{
		public void Update(in SystemContext context) => update(context);
	}

	private sealed class NestedCondition : ISystemRunCondition
	{
		public bool Throw { get; set; }
		public World? Nested { get; init; }
		public bool ShouldRun(in SystemRunConditionContext context)
		{
			if (Throw) throw new InvalidOperationException("Condition failed.");
			Nested?.Tick(0.125f);
			return true;
		}
	}
}

using System;
using BenchmarkDotNet.Attributes;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Benchmarks;

[MemoryDiagnoser]
public class EcsWorldComponentAccessBenchmarks
{
	private ComponentAccessor<Position>                _positionAccessor;
	private QueryHandle<PositionQuerySpec>             _positionQueryHandle = default;
	private Entity[]                                   _entities = null!;
	private QueryHandle<PositionVelocityQuerySpec> _queryHandle = default;
	private QueryView<PositionVelocityQuerySpec>   _queryView;
	private World                                   _world = null!;

	[Params(100_000)]
	public int EntityCount { get; set; }

	[Benchmark(Description = "World sequential component read via TryGet<T>")]
	public float SequentialTryGetRead()
	{
		var sum = 0f;
		for (var i = 0; i < _entities.Length; i++)
		{
			if (_world.TryGet<Position>(_entities[i], out var position))
				sum += position.X + position.Y;
		}

		return sum;
	}

	[Benchmark(Description = "World accessor sequential component read via TryRead")]
	public float SequentialAccessorTryRead()
	{
		var sum = 0f;
		for (var i = 0; i < _entities.Length; i++)
		{
			if (_positionAccessor.TryRead(_entities[i], out var position))
				sum += position.X + position.Y;
		}

		return sum;
	}

	[Benchmark(Description = "World sequential component ref-write via Write<T>")]
	public float SequentialWrite()
	{
		var sum = 0f;
		for (var i = 0; i < _entities.Length; i++)
		{
			ref var position = ref _world.Write<Position>(_entities[i]);
			position.X += 0.125f;
			position.Y -= 0.125f;
			sum += position.X;
		}

		return sum;
	}

	[Benchmark(Description = "World accessor sequential component ref-write via Write")]
	public float SequentialAccessorWrite()
	{
		var sum = 0f;
		for (var i = 0; i < _entities.Length; i++)
		{
			ref var position = ref _positionAccessor.Write(_entities[i]);
			position.X += 0.125f;
			position.Y -= 0.125f;
			sum += position.X;
		}

		return sum;
	}

	[Benchmark(Description = "World query cursor struct-job run")]
	public int QueryCursorRun()
	{
		using var cursor = _world.Execute(_queryHandle);
		if (!cursor.MoveNext())
			return 0;

		cursor.Run<IntegrateJob, Position, Velocity>(new(0.016f));
		return _world.EntityCount;
	}

	[Benchmark(Description = "World query cursor sequential Write<T>")]
	public float QueryCursorSequentialWrite()
	{
		using var cursor = _world.Execute(_positionQueryHandle);
		if (!cursor.MoveNext())
			return 0f;

		var sum = 0f;
		for (var i = 0; i < EntityCount; i++)
		{
			ref var position = ref cursor.Write<Position>(i);
			position.X += 0.125f;
			position.Y -= 0.125f;
			sum += position.X;
		}

		return sum;
	}

	[Benchmark(Description = "QueryView compiled query struct-job run")]
	public int QueryViewRun()
	{
		_queryView.Run<IntegrateJob, Position, Velocity>(new(0.016f));
		return _world.EntityCount;
	}

	[Benchmark(Description = "Cursor window: 64 indexed entity/component reads")]
	public float CursorIndexedWindow()
	{
		using var cursor = _world.Execute(_positionQueryHandle);
		cursor.MoveNext();
		float sum = 0;
		int end = Math.Min(cursor.Count, 64);
		for (var index = 0; index < end; index++)
			sum += cursor.GetEntity(index).Id + cursor.Read<Position>(index).X;
		return sum;
	}

	[Benchmark(Description = "Cursor window: materialize all handles, then read 64")]
	public float CursorMaterializedWindow()
	{
		using var cursor = _world.Execute(_positionQueryHandle);
		cursor.MoveNext();
		var entities = cursor.Current;
		float sum = 0;
		int end = Math.Min(entities.Length, 64);
		for (var index = 0; index < end; index++)
			sum += entities[index].Id + cursor.Read<Position>(index).X;
		return sum;
	}

	[GlobalSetup]
	public void Setup()
	{
		_world = new(new WorldConfig()
		{
			EntityCapacity                = EntityCount + 32,
			ComponentTypeCapacity         = 64,
			CommandCapacity               = EntityCount * 3,
			CommandPayloadCapacityPerType = EntityCount * 2,
			QueryResultCapacity           = EntityCount + 32
		});

		using var commands = _world.CreateCommandStream();
		for (var i = 0; i < EntityCount; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y = i });
			commands.Set(entity, new Velocity { X = 1, Y = -1 });
		}

		_world.Playback(commands);
		_queryHandle = _world.Compile<PositionVelocityQuerySpec>();
		_queryView = new(_world, _queryHandle);
		_positionAccessor = _world.GetAccessor<Position>();

		_positionQueryHandle = _world.Compile<PositionQuerySpec>();
		_entities = new Entity[EntityCount];
		using var cursor = _world.Execute(_positionQueryHandle);
		cursor.MoveNext();
		cursor.Current.CopyTo(_entities);
	}

	[GlobalCleanup]
	public void Cleanup() => _world.Dispose();

	private readonly struct PositionQuerySpec : ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder) => builder.All<Position>();
	}

	private readonly struct PositionVelocityQuerySpec : ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder)
		{
			builder.All<Position>();
			builder.All<Velocity>();
		}
	}

	private readonly struct IntegrateJob(float dt) : IForEach<Position, Velocity>
	{
		public void Execute(ref Position component1, in Velocity component2)
		{
			component1.X += component2.X * dt;
			component1.Y += component2.Y * dt;
		}
	}

	private struct Position
	{
		public float X;
		public float Y;
	}

	private struct Velocity
	{
		public float X;
		public float Y;
	}
}

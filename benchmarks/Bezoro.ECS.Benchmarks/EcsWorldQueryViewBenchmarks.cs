using BenchmarkDotNet.Attributes;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Benchmarks;

[MemoryDiagnoser]
public class EcsWorldQueryViewBenchmarks
{
	private QueryView<ManagedNoteQuerySpec>       _managedQuery;
	private QueryView<PositionVelocityQuerySpec>  _positionVelocityQuery;
	private World                                 _world = null!;

	[Params(100_000)]
	public int EntityCount { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		_world = new(new WorldConfig()
		{
			EntityCapacity                = EntityCount + 32,
			ComponentTypeCapacity         = 64,
			CommandCapacity               = EntityCount * 4,
			CommandPayloadCapacityPerType = EntityCount * 3,
			QueryResultCapacity           = EntityCount + 32
		});

		using var commands = _world.CreateCommandStream();
		for (var i = 0; i < EntityCount; i++)
		{
			var entity = commands.CreateEntity();
			commands.Set(entity, new Position { X = i, Y = i });
			commands.Set(entity, new Velocity { X = 1, Y = -1 });
			commands.Set(entity, new ManagedNote { Label = "note", Count = i });
		}

		_world.Playback(commands);
		_positionVelocityQuery = _world.Query<PositionVelocityQuerySpec>();
		_managedQuery = _world.Query<ManagedNoteQuerySpec>();
	}

	[GlobalCleanup]
	public void Cleanup() => _world.Dispose();

	[Benchmark(Description = "QueryView typed ForEach over unmanaged components")]
	public int QueryViewTypedForEach()
	{
		_positionVelocityQuery.ForEach<Position, Velocity>(
			static (Entity entity, ref Position position, in Velocity velocity) =>
			{
				position.X += velocity.X * 0.016f;
				position.Y += velocity.Y * 0.016f;
			}
		);

		return _world.EntityCount;
	}

	[Benchmark(Description = "QueryView struct-job Run over unmanaged components")]
	public int QueryViewRun()
	{
		_positionVelocityQuery.Run<IntegrateJob, Position, Velocity>(new(0.016f));
		return _world.EntityCount;
	}

	[Benchmark(Description = "QueryView entity-aware struct-job RunEntity over unmanaged components")]
	public int QueryViewRunEntity()
	{
		_positionVelocityQuery.RunEntity<IntegrateEntityJob, Position, Velocity>(new(0.016f));
		return _world.EntityCount;
	}

	[Benchmark(Description = "QueryView read-only ForEachRead over managed components")]
	public int QueryViewForEachReadManaged()
	{
		_managedQuery.ForEachRead<ManagedNote>(
			static (Entity entity, in ManagedNote note) =>
			{
				_ = entity;
				_ = note.Count;
			}
		);

		return _world.EntityCount;
	}

	[Benchmark(Description = "QueryView mutable ForEach over managed components")]
	public int QueryViewForEachManaged()
	{
		_managedQuery.ForEach<ManagedNote>(
			static (Entity entity, ref ManagedNote note) =>
			{
				_ = entity;
				note.Count++;
			}
		);

		return _world.EntityCount;
	}

	[Benchmark(Description = "QueryView parallel entity-aware struct job")]
	public int QueryViewRunParallelEntity()
	{
		_positionVelocityQuery.RunParallelEntity<IntegrateEntityJob, Position, Velocity>(new(0.016f), 4);
		return _world.EntityCount;
	}

	private readonly struct ManagedNoteQuerySpec : ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder) => builder.All<ManagedNote>();
	}

	private struct ManagedNote
	{
		public string Label;
		public int    Count;
	}

	private readonly struct PositionVelocityQuerySpec : ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder)
		{
			builder.All<Position>();
			builder.All<Velocity>();
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

	private readonly struct IntegrateJob(float dt) : IForEach<Position, Velocity>
	{
		public void Execute(ref Position component1, in Velocity component2)
		{
			component1.X += component2.X * dt;
			component1.Y += component2.Y * dt;
		}
	}

	private readonly struct IntegrateEntityJob(float dt) : IForEachEntity<Position, Velocity>
	{
		public void Execute(Entity entity, ref Position component1, in Velocity component2)
		{
			_ = entity;
			component1.X += component2.X * dt;
			component1.Y += component2.Y * dt;
		}
	}
}

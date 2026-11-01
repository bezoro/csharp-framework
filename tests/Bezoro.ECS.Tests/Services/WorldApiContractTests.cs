using System;
using System.Linq;
using System.Reflection;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using JetBrains.Annotations;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

[TestSubject(typeof(World))]
public class WorldApiContractTests
{

	[Fact]
	public void Add_WhenCalledWithoutValue_ShouldAddDefaultInitializedComponent()
	{
		using var world  = new World();
		var       entity = world.Spawn();

		world.Add<ApiHealth>(entity);

		world.Has<ApiHealth>(entity).Should().BeTrue();
		world.Read<ApiHealth>(entity).Should().Be(default(ApiHealth));
	}

	[Fact]
	public void CommandStreamPlayback_WhenCreatingAndSettingComponent_ShouldApplyChanges()
	{
		using var world  = new World();
		var       stream = world.CreateCommandStream();
		var       entity = stream.CreateEntity();
		stream.Set(entity, new ApiPosition { X = 11f, Y = 13f });

		world.Playback(stream);

		world.EntityCount.Should().Be(1);
		var       handle = world.Compile<PositionQuerySpec>();
		using var cursor = world.Execute(handle);
		cursor.MoveNext().Should().BeTrue();
		cursor.Current.Length.Should().Be(1);

		var created = cursor.Current[0];
		world.Read<ApiPosition>(created).Should().Be(new ApiPosition { X = 11f, Y = 13f });
	}

	[Fact]
	public void Execute_WhenUsingHandleFromDifferentWorld_ShouldThrowInvalidOperationException()
	{
		var owner  = new World();
		var other  = new World();
		var handle = owner.Compile<PositionQuerySpec>();

		var act = () => other.Execute(handle);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*different world*");
	}

	[Fact]
	public void IWorldSurface_WhenUsedThroughInterface_ShouldSupportCoreOperations()
	{
		IWorld world  = new World();
		var    entity = world.Spawn();
		world.Add(entity, new ApiPosition { X = 5f, Y = 8f });

		world.Has<ApiPosition>(entity).Should().BeTrue();
		world.TryGet(entity, out ApiPosition position).Should().BeTrue();
		position.Should().Be(new ApiPosition { X = 5f, Y = 8f });
	}

	[Fact]
	public void QueryViewRun_WhenCalled_ShouldPreserveMutation()
	{
		using var world = new World();
		var entity = world.Spawn(new ApiPosition { X = 3f });
		var handle = world.Compile<PositionQuerySpec>();

		new QueryView<PositionQuerySpec>(world, handle).Run<ApiIncrementJob, ApiPosition>(new ApiIncrementJob());

		world.Read<ApiPosition>(entity).X.Should().Be(4f);
	}

	[Fact]
	public void QueryViewRunEntity_WhenCalled_ShouldPreserveEntityDelivery()
	{
		using var world = new World();
		var entity = world.Spawn(new ApiPosition());
		var handle = world.Compile<PositionQuerySpec>();

		new QueryView<PositionQuerySpec>(world, handle).RunEntity<ApiEntityCaptureJob, ApiPosition>(new ApiEntityCaptureJob());

		world.Read<ApiPosition>(entity).Should().Be(new ApiPosition { X = entity.Id, Y = entity.Version });
	}

	[Fact]
	public void QueryViewRunParallel_WhenCalled_ShouldPreserveMutation()
	{
		using var world = new World();
		var entity = world.Spawn(new ApiPosition { X = 3f });
		var handle = world.Compile<PositionQuerySpec>();

		new QueryView<PositionQuerySpec>(world, handle).RunParallel<ApiIncrementJob, ApiPosition>(new ApiIncrementJob(), 1);

		world.Read<ApiPosition>(entity).X.Should().Be(4f);
	}

	[Fact]
	public void QueryViewRunParallelEntity_WhenCalled_ShouldPreserveEntityDelivery()
	{
		using var world = new World();
		var entity = world.Spawn(new ApiPosition());
		var handle = world.Compile<PositionQuerySpec>();

		new QueryView<PositionQuerySpec>(world, handle).RunParallelEntity<ApiEntityCaptureJob, ApiPosition>(
			new ApiEntityCaptureJob(),
			1
		);

		world.Read<ApiPosition>(entity).Should().Be(new ApiPosition { X = entity.Id, Y = entity.Version });
	}

	[Fact]
	public void PublicQuerySurface_WhenInspectingContracts_ShouldNotExposeIgnoredParameterizedQueryOverload()
	{
		var interfaceMethods = typeof(IWorld)
							  .GetMethods()
							  .Where(static method => method.Name == nameof(IWorld.Query))
							  .ToArray();
		var worldMethods = typeof(World)
						  .GetMethods()
						  .Where(static method => method.Name == nameof(World.Query))
						  .ToArray();

		interfaceMethods.Should().ContainSingle(static method => method.GetParameters().Length == 0);
		interfaceMethods.Should().NotContain(static method => method.GetParameters().Length == 1);

		worldMethods.Should().ContainSingle(
			static method => method.IsGenericMethodDefinition && method.GetParameters().Length == 0
		);
		worldMethods.Should().NotContain(
			static method => method.IsGenericMethodDefinition && method.GetParameters().Length == 1
		);
	}

	[Fact]
	public void PublicQuerySurface_WhenInspectingContracts_ShouldExposeQueryViewFromIWorldAndWorld()
	{
		var interfaceMethod = typeof(IWorld)
							 .GetMethods()
							 .Single(static method => method.Name == nameof(IWorld.Query) && method.GetParameters().Length == 0);
		var worldMethod = typeof(World)
						 .GetMethods()
						 .Single(
							  static method => method.Name == nameof(World.Query)
							                && method.IsGenericMethodDefinition
							                && method.GetParameters().Length == 0
						  );

		interfaceMethod.ReturnType.GetGenericTypeDefinition().Should().Be(typeof(QueryView<>));
		worldMethod.ReturnType.GetGenericTypeDefinition().Should().Be(typeof(QueryView<>));
	}

	[Fact]
	public void SystemContext_WhenConstructedWithCommandStream_ShouldExposeCanonicalValues()
	{
		using var world = new World();
		using var stream = world.CreateCommandStream();

		var context = new SystemContext(0.25f, Stage.Tick, world, stream);

		context.DeltaTime.Should().Be(0.25f);
		context.Stage.Should().Be(Stage.Tick);
		context.World.Should().BeSameAs(world);
		context.CommandStream.Should().BeSameAs(stream);
	}

	[Fact]
	public void SystemContext_WhenCommandStreamIsNull_ShouldThrowArgumentNullException()
	{
		using var world = new World();

		var act = () => new SystemContext(0.25f, Stage.Tick, world, (CommandStream)null!);

		act.Should().Throw<ArgumentNullException>()
		   .Which.ParamName.Should().Be("commandStream");
	}

	[Fact]
	public void Playback_WhenUsingDifferentWorld_ShouldThrowInvalidOperationException()
	{
		var owner  = new World();
		var other  = new World();
		var stream = owner.CreateCommandStream();

		var act = () => other.Playback(stream);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*different world*");
	}

	[Fact]
	public void RestoreSnapshot_WhenCalledWithoutExplicitAllowLists_ShouldRejectSnapshotTypesByDefault()
	{
		using var world = new World();
		var snapshot = new WorldSnapshot(
			[],
			[
				new(
					new Entity(1, 1),
					[
						new SnapshotComponentRecord(typeof(ApiPosition), new ApiPosition { X = 1f, Y = 2f })
					]
				)
			],
			[]
		);
		var reader = new InMemorySnapshotReader(snapshot);

		var act = () => world.RestoreSnapshot(ref reader);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*not allow-listed*");
	}

	[Fact]
	public void Resources_WhenSet_ShouldBeReadableByReference()
	{
		using var world = new World();
		world.SetResource(new ApiTuning { Gravity = 9.81f });

		ref var tuning = ref world.WriteResource<ApiTuning>();
		tuning.Gravity = 12.5f;

		world.ReadResource<ApiTuning>().Gravity.Should().Be(12.5f);
	}

	[Fact]
	public void SpawnOverloads_WhenCalled_ShouldInitializeComponents()
	{
		using var world = new World();

		var entity = world.Spawn(
			new ApiPosition { X     = 3f, Y  = 4f },
			new ApiVelocity { X     = 1f, Y  = -2f },
			new ApiHealth { Current = 7, Max = 10 }
		);

		world.Read<ApiPosition>(entity).Should().Be(new ApiPosition { X   = 3f, Y  = 4f });
		world.Read<ApiVelocity>(entity).Should().Be(new ApiVelocity { X   = 1f, Y  = -2f });
		world.Read<ApiHealth>(entity).Should().Be(new ApiHealth { Current = 7, Max = 10 });
	}

	private readonly struct PositionQuerySpec : ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder) => builder.All<ApiPosition>();
	}

	private readonly struct ApiIncrementJob : IForEach<ApiPosition>
	{
		public void Execute(ref ApiPosition component1) => component1.X++;
	}

	private readonly struct ApiEntityCaptureJob : IForEachEntity<ApiPosition>
	{
		public void Execute(Entity entity, ref ApiPosition component1)
		{
			component1.X = entity.Id;
			component1.Y = entity.Version;
		}
	}

	private readonly struct InMemorySnapshotReader(WorldSnapshot snapshot) : IWorldSnapshotReader
	{
		public WorldSnapshot Read() => snapshot;
	}
}

internal struct ApiHealth
{
	public int Current;
	public int Max;
}

internal struct ApiPosition
{
	public float X;
	public float Y;
}

internal struct ApiTuning
{
	public float Gravity;
}

internal struct ApiVelocity
{
	public float X;
	public float Y;
}

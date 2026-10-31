using System;
using System.Numerics;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Bezoro.GameSystems.MovementSystem.Types;
using Bezoro.GameSystems.StreamingSystem.Types;
using Xunit;
using Streaming = Bezoro.GameSystems.StreamingSystem.Services.StreamingSystem;

namespace Bezoro.GameSystems.Tests.StreamingSystem;

public sealed class StreamingNumericContractTests
{
	[Fact]
	public void Budget_MarksOnlyRealTransitionsAsChanged()
	{
		using var world = new World();
		world.AddSystem(new Streaming());
		world.SetResource(new StreamingConfig { StreamInDistance = 10f, StreamOutDistance = 20f, MaxEntitiesPerTick = 1 });
		for (int i = 0; i < 5; i++) world.Spawn(new Position(), new StreamState());
		var changes = world.Compile<ChangedStreamQuery>();
		using (var initial = world.Execute(changes)) Assert.Equal(5, initial.Count);
		for (int i = 0; i < 5; i++)
		{
			world.Tick(0f);
			using var changed = world.Execute(changes);
			changed.MoveNext();
			Assert.Equal(1, changed.Count);
			Assert.True(changed.Read<StreamState>(0).IsStreamedIn);
		}
		world.Tick(0f);
		using var unchanged = world.Execute(changes);
		Assert.Equal(0, unchanged.Count);
	}

	private readonly struct ChangedStreamQuery : ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder) => builder.Changed<StreamState>();
	}

	[Fact]
	public void ChangedSubscriberFailure_DoesNotSuppressTheTransitionNotification()
	{
		using var world = new World();
		var system = new Streaming();
		world.AddSystem(system);
		world.Spawn(new Position(), new StreamState());
		var streamedIn = 0;
		system.Changed += _ => throw new Exception("Subscriber failure.");
		system.StreamedIn += _ => streamedIn++;
		world.Tick(0f);
		Assert.Equal(1, streamedIn);
		Assert.Equal(1, world.ReadResource<StreamingEventsResource>().Count);
	}

	[Fact]
	public void LargeFiniteDistances_DoNotCollapseToInfinity()
	{
		using var world = new World();
		world.AddSystem(new Streaming());
		world.SetResource(new StreamingConfig { StreamInDistance = 1e20f, StreamOutDistance = 2e20f });
		var far = world.Spawn(new Position { X = 3e20f }, new StreamState());
		var near = world.Spawn(new Position { X = 5e19f }, new StreamState());
		world.Tick(0f);
		Assert.False(world.Read<StreamState>(far).IsStreamedIn);
		Assert.True(world.Read<StreamState>(near).IsStreamedIn);
		Assert.True(world.ReadResource<StreamingEventsResource>().TryDequeue(out var transition));
		Assert.True(double.IsFinite(transition.DistanceSquared));
	}

	[Theory]
	[InlineData(float.NaN)]
	[InlineData(float.PositiveInfinity)]
	[InlineData(float.NegativeInfinity)]
	public void NonFiniteConfiguration_RejectsBeforeChangingState(float invalid)
	{
		using var world = new World();
		world.AddSystem(new Streaming());
		var entity = world.Spawn(new Position(), new StreamState());
		world.SetResource(new StreamingConfig { StreamInDistance = invalid, StreamOutDistance = 20f });
		Assert.ThrowsAny<ArgumentException>(() => world.Tick(0f));
		Assert.False(world.Read<StreamState>(entity).IsStreamedIn);
		Assert.Equal(0, world.ReadResource<StreamingEventsResource>().Count);
	}

	[Fact]
	public void NonFiniteReference_RejectsBeforeChangingState()
	{
		using var world = new World();
		world.AddSystem(new Streaming());
		world.SetResource(new StreamingConfig { ReferencePosition = new Vector3(float.NaN), StreamInDistance = 10f, StreamOutDistance = 20f });
		Assert.ThrowsAny<ArgumentException>(() => world.Tick(0f));
	}
}

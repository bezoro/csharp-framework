using Bezoro.ECS.Services;
using Bezoro.GameSystems.MovementSystem.Types;
using Xunit;
using MovementSystemType = Bezoro.GameSystems.MovementSystem.Services.MovementSystem;

namespace Bezoro.GameSystems.Tests.MovementSystem;

public class MovementNumericSafetyAdditionalTests
{
	[Fact]
	public void PositiveDeltaBelowComparisonEpsilon_StillIntegratesVelocity()
	{
		using var world = new World();
		world.AddSystem(new MovementSystemType());
		var entity = world.Spawn(new Position(), new Velocity { X = 10_000_000f });
		world.FixedTick(0.0000001f);
		Assert.Equal(1f, world.Read<Position>(entity).X);
	}

	[Theory]
	[InlineData(0, float.NaN)]
	[InlineData(1, float.NaN)]
	[InlineData(2, float.NaN)]
	[InlineData(0, float.PositiveInfinity)]
	[InlineData(1, float.PositiveInfinity)]
	[InlineData(2, float.PositiveInfinity)]
	[InlineData(0, float.NegativeInfinity)]
	[InlineData(1, float.NegativeInfinity)]
	[InlineData(2, float.NegativeInfinity)]
	[InlineData(0, float.MaxValue)]
	[InlineData(1, float.MaxValue)]
	[InlineData(2, float.MaxValue)]
	public void UnsafeVelocityPreservesWholePositionWithoutBlockingOtherArchetypesAndCanRecover(int axis, float unsafeValue)
	{
		var world = new World();
		world.AddSystem(new MovementSystemType());
		var start = new Position { X = 4f, Y = 8f, Z = 16f };
		var unsafeVelocity = new Velocity
		{
			X = axis == 0 ? unsafeValue : 1f,
			Y = axis == 1 ? unsafeValue : -2f,
			Z = axis == 2 ? unsafeValue : 3f
		};
		var affected = world.Spawn(start, unsafeVelocity);
		var healthy = world.Spawn(new Position { X = 2f, Y = 4f, Z = 8f }, new Velocity { X = 1f, Y = -1f, Z = 0.5f });
		world.Add(healthy, new ExtraMarker());

		world.FixedTick(2f);
		Assert.Equal(start, world.Read<Position>(affected));
		Assert.Equal(unsafeVelocity, world.Read<Velocity>(affected));
		Assert.Equal(new Position { X = 4f, Y = 2f, Z = 9f }, world.Read<Position>(healthy));

		var restored = new Velocity { X = -1f, Y = 2f, Z = -3f };
		world.Write<Velocity>(affected) = restored;
		world.FixedTick(0.5f);
		Assert.Equal(new Position { X = 3.5f, Y = 9f, Z = 14.5f }, world.Read<Position>(affected));
		Assert.Equal(restored, world.Read<Velocity>(affected));
		Assert.Equal(new Position { X = 4.5f, Y = 1.5f, Z = 9.25f }, world.Read<Position>(healthy));
	}

	private readonly struct ExtraMarker { }
}

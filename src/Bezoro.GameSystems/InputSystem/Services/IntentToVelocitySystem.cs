using System;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Bezoro.GameSystems.InputSystem.Types;
using Bezoro.GameSystems.MovementSystem.Types;

namespace Bezoro.GameSystems.InputSystem.Services;

/// <summary>
///     Converts held movement intent into per-entity velocity for movement simulation.
/// </summary>
[Reads(typeof(InputControl))]
[Reads(typeof(MovementInputSettings))]
[Writes(typeof(MovementIntent))]
[Writes(typeof(Velocity))]
[ReadsResource(typeof(InputCommandQueue))]
public sealed class IntentToVelocitySystem : ISystem
{
	private QueryHandle<IntentToVelocityQuerySpec> _query;

	/// <inheritdoc />
	public Stage Stage => Stage.PreTick;

	/// <inheritdoc />
	public SystemLoopPhase LoopPhase => SystemLoopPhase.FixedTick;

	/// <inheritdoc />
	public void OnCreate(World world)
	{
		if (world is null) throw new ArgumentNullException(nameof(world));
		_query = world.Compile<IntentToVelocityQuerySpec>();
	}

	/// <inheritdoc />
	public void Update(in SystemContext context)
	{
		var world = context.World;
		ref readonly var queue = ref world.ReadResource<InputCommandQueue>();
		var now       = queue.SimulationTimeSeconds;

		using var cursor = world.Execute(_query);
		if (!cursor.MoveNext())
			return;

		for (var i = 0; i < cursor.Count; i++)
		{
			ref readonly var control = ref cursor.Read<InputControl>(i);
			ref readonly var setting = ref cursor.Read<MovementInputSettings>(i);
			ref var intent = ref cursor.Write<MovementIntent>(i);
			ref var velocity = ref cursor.Write<Velocity>(i);

			if (float.IsNaN(setting.Speed) || float.IsInfinity(setting.Speed) ||
				float.IsNaN(setting.HoldDurationSeconds) || float.IsInfinity(setting.HoldDurationSeconds) ||
				setting.HoldDurationSeconds < 0f ||
				!queue.TryGetLatest(control.ControlId, out var state))
			{
				intent   = default;
				velocity = default;
				continue;
			}

			var ageSeconds = now - state.ReceivedAtSeconds;
			if (ageSeconds > setting.HoldDurationSeconds)
			{
				intent   = default;
				velocity = default;
				continue;
			}

			var velocityX = state.MoveX * setting.Speed;
			var velocityY = state.MoveY * setting.Speed;
			var velocityZ = state.MoveZ * setting.Speed;
			if (float.IsNaN(velocityX) || float.IsInfinity(velocityX) ||
				float.IsNaN(velocityY) || float.IsInfinity(velocityY) ||
				float.IsNaN(velocityZ) || float.IsInfinity(velocityZ))
			{
				intent   = default;
				velocity = default;
				continue;
			}

			intent.X   = state.MoveX;
			intent.Y   = state.MoveY;
			intent.Z   = state.MoveZ;
			velocity.X = velocityX;
			velocity.Y = velocityY;
			velocity.Z = velocityZ;
		}
	}

	private readonly struct IntentToVelocityQuerySpec : ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder)
		{
			builder.All<InputControl>();
			builder.All<MovementIntent>();
			builder.All<MovementInputSettings>();
			builder.All<Velocity>();
		}
	}
}

using System;
using System.Collections.Generic;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Bezoro.GameSystems.InputSystem.Types;

namespace Bezoro.GameSystems.InputSystem.Services;

/// <summary>
///     Drains external input commands into latest-per-control snapshots each fixed update.
/// </summary>
[WritesResource(typeof(InputCommandQueue))]
public sealed class InputIngestionSystem : ISystem
{
	/// <summary>Creates ingestion with an explicit maximum number of dequeued commands per update.</summary>
	/// <param name="maxCommandsPerUpdate">Zero pauses ingestion. The default drains until empty.</param>
	public InputIngestionSystem(int maxCommandsPerUpdate = int.MaxValue)
	{
		if (maxCommandsPerUpdate < 0) throw new ArgumentOutOfRangeException(nameof(maxCommandsPerUpdate));
		MaxCommandsPerUpdate = maxCommandsPerUpdate;
	}

	/// <summary>Maximum dequeued commands, including stale commands, per update.</summary>
	public int MaxCommandsPerUpdate { get; }
	/// <inheritdoc />
	public Stage Stage => Stage.Input;

	/// <inheritdoc />
	public SystemLoopPhase LoopPhase => SystemLoopPhase.FixedTick;

	/// <inheritdoc />
	public void OnCreate(World world)
	{
		if (world is null)
			throw new ArgumentNullException(nameof(world));

		try
		{
			_ = world.ReadResource<InputCommandQueue>();
		}
		catch (KeyNotFoundException)
		{
			world.SetResource(new InputCommandQueue());
		}
	}

	/// <inheritdoc />
	public void Update(in SystemContext context)
	{
		var world = context.World;
		ref var queue = ref world.WriteResource<InputCommandQueue>();
		queue.AdvanceTime(context.DeltaTime);
		queue.Drain(MaxCommandsPerUpdate);
	}
}

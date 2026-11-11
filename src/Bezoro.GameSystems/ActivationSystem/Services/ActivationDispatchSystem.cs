using System;
using System.Collections.Generic;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Bezoro.GameSystems.ActivationSystem.Types;

namespace Bezoro.GameSystems.ActivationSystem.Services;

/// <summary>
///     ECS system that executes callbacks activated by <see cref="ActivationProcessingSystem" />.
/// </summary>
[WritesResource(typeof(ActivationConfig))]
[WritesResource(typeof(ActivationDispatchQueueResource))]
public sealed class ActivationDispatchSystem : ISystem
{
	/// <inheritdoc />
	public Stage Stage => Stage.PostTick;

	/// <inheritdoc />
	public SystemLoopPhase LoopPhase => SystemLoopPhase.Tick;

	/// <inheritdoc />
	public void OnCreate(World world)
	{
		if (world is null) throw new ArgumentNullException(nameof(world));
		EnsureResources(world);
	}

	/// <inheritdoc />
	public void Update(in SystemContext context)
	{
		var world = context.World;
		if (world is null) throw new ArgumentNullException(nameof(world));
		EnsureResources(world);

		ref readonly var config = ref world.ReadResource<ActivationConfig>();
		ref var dispatchQueue = ref world.WriteResource<ActivationDispatchQueueResource>();
		// Callback/producers cannot extend this pass's work budget. With one consumer,
		// callbacks posted during dispatch are deferred until the next update.
		int remaining = dispatchQueue.Count;
		while (remaining-- > 0 && dispatchQueue.TryDequeue(out var callback))
		{
			try
			{
				if (config.CallbackDispatcher is null)
				{
					callback();
					continue;
				}

				config.CallbackDispatcher(callback);
			}
			catch
			{
				// Callback and dispatcher exceptions should not break simulation.
			}
		}
	}

	private static void EnsureResources(World world)
	{
		try
		{
			_ = world.ReadResource<ActivationConfig>();
		}
		catch (KeyNotFoundException)
		{
			world.SetResource(new ActivationConfig());
		}

		try
		{
			_ = world.ReadResource<ActivationDispatchQueueResource>();
		}
		catch (KeyNotFoundException)
		{
			world.SetResource(new ActivationDispatchQueueResource());
		}
	}
}

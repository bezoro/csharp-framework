using System;
using System.Collections.Generic;
using Bezoro.ECS.Services;
using Bezoro.GameSystems.InputSystem.Services;
using Bezoro.GameSystems.InputSystem.Types;

namespace Bezoro.GameSystems.InputSystem.Extensions;

/// <summary>
///     Helpers for wiring external input resources and input systems into an ECS world.
/// </summary>
public static class InputWorldExtensions
{
	/// <summary>
	///     Gets the existing <see cref="InputCommandQueue" /> resource or creates one if missing.
	/// </summary>
	/// <param name="world">The ECS world.</param>
	/// <returns>The input command queue resource.</returns>
	public static InputCommandQueue GetOrCreateInputCommandQueue(this World world)
	{
		if (world is null)
			throw new ArgumentNullException(nameof(world));

		try
		{
			return world.WriteResource<InputCommandQueue>();
		}
		catch (KeyNotFoundException)
		{
			var queue = new InputCommandQueue();
			world.SetResource(queue);
			return queue;
		}
	}

	/// <summary>
	///     Registers fixed-tick input systems with their declared stage ordering.
	/// </summary>
	/// <param name="world">The ECS world.</param>
	/// <param name="maxCommandsPerUpdate">Maximum dequeued commands per fixed update. Zero pauses ingestion.</param>
	public static void AddMovementInputPipeline(this World world, int maxCommandsPerUpdate = int.MaxValue)
	{
		if (world is null)
			throw new ArgumentNullException(nameof(world));

		var ingestionSystem = new InputIngestionSystem(maxCommandsPerUpdate);
		var intentSystem    = new IntentToVelocitySystem();

		world.AddSystem(ingestionSystem, ingestionSystem.Stage);
		world.AddSystem(intentSystem, intentSystem.Stage);
	}
}

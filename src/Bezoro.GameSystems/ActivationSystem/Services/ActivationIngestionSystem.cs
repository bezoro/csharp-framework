using System;
using System.Collections.Generic;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Bezoro.GameSystems.ActivationSystem.Types;

namespace Bezoro.GameSystems.ActivationSystem.Services;

/// <summary>
///     ECS system that drains externally queued activation commands.
/// </summary>
[Writes(typeof(ActivationEntry))]
[Writes(typeof(ActivationCancellationRequest))]
[WritesResource(typeof(ActivationCommandQueue))]
public sealed class ActivationIngestionSystem : ISystem
{
	/// <inheritdoc />
	public Stage Stage => Stage.Input;

	/// <inheritdoc />
	public SystemLoopPhase LoopPhase => SystemLoopPhase.Tick;

	/// <inheritdoc />
	public void OnCreate(World world)
	{
		if (world is null) throw new ArgumentNullException(nameof(world));
		EnsureCommandQueue(world);
	}

	/// <inheritdoc />
	public void Update(in SystemContext context)
	{
		var world = context.World;
		if (world is null) throw new ArgumentNullException(nameof(world));
		EnsureCommandQueue(world);

		ref var queue = ref world.WriteResource<ActivationCommandQueue>();
		while (queue.TryDequeue(out var command))
		{
			switch (command.Kind)
			{
				case ActivationCommandKind.Register:
				{
					var entry = new ActivationEntry(command.Handle, command.Callback!, command.Priority);
					context.CommandStream.CreateEntity(in entry);
					break;
				}
				case ActivationCommandKind.Cancel:
				{
					var request = new ActivationCancellationRequest(command.Handle);
					context.CommandStream.CreateEntity(in request);
					break;
				}
				default:
					throw new ArgumentOutOfRangeException();
			}
		}
	}

	private static void EnsureCommandQueue(World world)
	{
		try
		{
			_ = world.ReadResource<ActivationCommandQueue>();
		}
		catch (KeyNotFoundException)
		{
			world.SetResource(new ActivationCommandQueue());
		}
	}
}

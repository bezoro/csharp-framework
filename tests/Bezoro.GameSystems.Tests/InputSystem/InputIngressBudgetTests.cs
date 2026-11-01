using System;
using Bezoro.ECS.Services;
using Bezoro.GameSystems.InputSystem.Extensions;
using Bezoro.GameSystems.InputSystem.Services;
using Bezoro.GameSystems.InputSystem.Types;
using Bezoro.GameSystems.MovementSystem.Types;
using Xunit;

namespace Bezoro.GameSystems.Tests.InputSystem;

public sealed class InputIngressBudgetTests
{
	[Fact]
	public void Budget_CountsDequeuedStaleCommandsAndLeavesBacklog()
	{
		using var world = new World();
		world.AddMovementInputPipeline(maxCommandsPerUpdate: 1);
		var queue = world.GetOrCreateInputCommandQueue();
		var entity = world.Spawn(new InputControl { ControlId = 1 }, new MovementIntent(), new Velocity(),
			new MovementInputSettings { Speed = 1f, HoldDurationSeconds = 10f });
		queue.Enqueue(1, 1, 0, 0, 2);
		queue.Enqueue(1, 3, 0, 0, 1);
		queue.Enqueue(1, 5, 0, 0, 3);
		world.FixedTick(0f);
		Assert.Equal(2, queue.PendingCommandCount);
		Assert.Equal(1f, world.Read<Velocity>(entity).X);
		world.FixedTick(0f);
		Assert.Equal(1, queue.PendingCommandCount);
		Assert.Equal(1f, world.Read<Velocity>(entity).X);
		world.FixedTick(0f);
		Assert.Equal(0, queue.PendingCommandCount);
		Assert.Equal(5f, world.Read<Velocity>(entity).X);
		Assert.Equal(1, queue.TrackedControlCount);
		Assert.True(queue.ForgetControl(1));
		Assert.Equal(0, queue.TrackedControlCount);
		world.FixedTick(0f);
		Assert.Equal(0f, world.Read<Velocity>(entity).X);
		queue.Enqueue(1, 2, 0, 0, 0);
		world.FixedTick(0f);
		Assert.Equal(2f, world.Read<Velocity>(entity).X);
	}

	[Fact]
	public void NegativeBudget_RejectsBeforePipelineRegistration() =>
		Assert.Throws<ArgumentOutOfRangeException>(() => new InputIngestionSystem(-1));
}

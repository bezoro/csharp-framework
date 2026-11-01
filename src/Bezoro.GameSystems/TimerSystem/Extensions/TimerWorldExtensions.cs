using System;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Bezoro.GameSystems.TimerSystem.Types;

namespace Bezoro.GameSystems.TimerSystem.Extensions;

/// <summary>
///     Extension helpers for controlling timer entities from gameplay code.
/// </summary>
public static class TimerWorldExtensions
{
	/// <summary>
	///     Transitions a timer from stopped to running and requests a started callback.
	/// </summary>
	/// <param name="world">World containing the timer entity.</param>
	/// <param name="timerEntity">Target timer entity.</param>
	/// <returns><c>true</c> when the transition was applied.</returns>
	public static bool StartTimer(this World world, Entity timerEntity)
	{
		if (world is null) throw new ArgumentNullException(nameof(world));
		if (!TryGetTimer(world, timerEntity, out var timer))
			return false;

		if (timer.State != TimerState.Stopped)
			return false;

		timer.State = TimerState.Running;
		world.Set(timerEntity, in timer);
		QueueTransition(world, timerEntity, in timer, TimerLifecycle.Started);
		return true;
	}

	/// <summary>
	///     Transitions a timer from running to paused and requests a paused callback.
	/// </summary>
	/// <param name="world">World containing the timer entity.</param>
	/// <param name="timerEntity">Target timer entity.</param>
	/// <returns><c>true</c> when the transition was applied.</returns>
	public static bool PauseTimer(this World world, Entity timerEntity)
	{
		if (world is null) throw new ArgumentNullException(nameof(world));
		if (!TryGetTimer(world, timerEntity, out var timer))
			return false;

		if (timer.State != TimerState.Running)
			return false;

		timer.State = TimerState.Paused;
		world.Set(timerEntity, in timer);
		QueueTransition(world, timerEntity, in timer, TimerLifecycle.Paused);
		return true;
	}

	/// <summary>
	///     Transitions a timer to stopped and requests a stopped callback.
	/// </summary>
	/// <param name="world">World containing the timer entity.</param>
	/// <param name="timerEntity">Target timer entity.</param>
	/// <returns><c>true</c> when the transition was applied.</returns>
	public static bool StopTimer(this World world, Entity timerEntity)
	{
		if (world is null) throw new ArgumentNullException(nameof(world));
		if (!TryGetTimer(world, timerEntity, out var timer))
			return false;

		if (timer.State is not (TimerState.Running or TimerState.Paused))
			return false;

		timer.State = TimerState.Stopped;
		world.Set(timerEntity, in timer);
		QueueTransition(world, timerEntity, in timer, TimerLifecycle.Stopped);
		return true;
	}

	/// <summary>
	///     Transitions a timer from paused to running and requests a resumed callback.
	/// </summary>
	/// <param name="world">World containing the timer entity.</param>
	/// <param name="timerEntity">Target timer entity.</param>
	/// <returns><c>true</c> when the transition was applied.</returns>
	public static bool ResumeTimer(this World world, Entity timerEntity)
	{
		if (world is null) throw new ArgumentNullException(nameof(world));
		if (!TryGetTimer(world, timerEntity, out var timer))
			return false;

		if (timer.State != TimerState.Paused)
			return false;

		timer.State = TimerState.Running;
		world.Set(timerEntity, in timer);
		QueueTransition(world, timerEntity, in timer, TimerLifecycle.Resumed);
		return true;
	}

	/// <summary>
	///     Resets elapsed time to zero, starts running, and requests a restarted callback.
	/// </summary>
	/// <param name="world">World containing the timer entity.</param>
	/// <param name="timerEntity">Target timer entity.</param>
	/// <returns><c>true</c> when the transition was applied.</returns>
	public static bool RestartTimer(this World world, Entity timerEntity)
	{
		if (world is null) throw new ArgumentNullException(nameof(world));
		if (!TryGetTimer(world, timerEntity, out var timer))
			return false;

		timer.ElapsedSeconds = 0f;
		timer.State = TimerState.Running;
		world.Set(timerEntity, in timer);
		QueueTransition(world, timerEntity, in timer, TimerLifecycle.Restarted);
		return true;
	}

	private static void QueueTransition(World world, Entity entity, in Timer timer, TimerLifecycle lifecycle)
	{
		var owner = world.TryRead(entity, out TimerOwner link) ? link.OwnerEntity : Entity.None;
		var transition = new TimerLifecycleEvent(entity, owner, timer.TimerId, lifecycle,
			timer.State, timer.ElapsedSeconds, timer.DurationSeconds);
		world.GetOrCreateResource<TimerTransitionQueue>().Enqueue(in transition);
	}

	private static bool TryGetTimer(World world, Entity timerEntity, out Timer timer)
	{
		if (!world.IsAlive(timerEntity))
		{
			timer = default;
			return false;
		}

		return world.TryGet(timerEntity, out timer);
	}
}

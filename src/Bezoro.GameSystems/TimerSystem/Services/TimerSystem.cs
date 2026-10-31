using System;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Bezoro.GameSystems.TimerSystem.Types;

namespace Bezoro.GameSystems.TimerSystem.Services;

/// <summary>
///     ECS system that advances running timers and emits lifecycle callbacks.
/// </summary>
[Writes(typeof(Timer))]
[Reads(typeof(TimerOwner))]
[WritesResource(typeof(TimerEventsResource))]
[WritesResource(typeof(TimerTransitionQueue))]
public sealed class TimerSystem : ISystem
{
	/// <summary>
	///     Raised when a timer transitions to running from stopped.
	/// </summary>
	public event Action<TimerLifecycleEvent>? Started;

	/// <summary>
	///     Raised when a timer transitions to paused.
	/// </summary>
	public event Action<TimerLifecycleEvent>? Paused;

	/// <summary>
	///     Raised when a timer transitions to stopped.
	/// </summary>
	public event Action<TimerLifecycleEvent>? Stopped;

	/// <summary>
	///     Raised when a timer reaches its duration.
	/// </summary>
	public event Action<TimerLifecycleEvent>? Finished;

	/// <summary>
	///     Raised when a paused timer resumes running.
	/// </summary>
	public event Action<TimerLifecycleEvent>? Resumed;

	/// <summary>
	///     Raised when a timer is explicitly restarted.
	/// </summary>
	public event Action<TimerLifecycleEvent>? Restarted;

	/// <inheritdoc />
	public Stage Stage => Stage.Tick;

	/// <inheritdoc />
	public SystemLoopPhase LoopPhase => SystemLoopPhase.Tick;

	/// <inheritdoc />
	public SystemUpdateSettings UpdateSettings => SystemUpdateSettings.EveryTick;

	/// <inheritdoc />
	public void OnCreate(World world)
	{
		if (world is null) throw new ArgumentNullException(nameof(world));
		world.GetOrCreateResource<TimerEventsResource>();
	}

	/// <inheritdoc />
	public void Update(in SystemContext context)
	{
		var world = context.World;
		if (world is null) throw new ArgumentNullException(nameof(world));

		world.GetOrCreateResource<TimerEventsResource>();
		var commands = context.CommandStream;
		float deltaTime = context.DeltaTime <= 0f || float.IsNaN(context.DeltaTime) || float.IsInfinity(context.DeltaTime)
			? 0f : context.DeltaTime;

		PublishPendingTransitions(world);

		world.Query<TimerQuery>().ForEach<Timer>(
			(Entity timerEntity, ref Timer timer) =>
		{
			AdvanceTimer(world, commands, timerEntity, ref timer, deltaTime);
		}
		);
	}

	private void AdvanceTimer(
		World         world,
		CommandStream commands,
		Entity        timerEntity,
		ref Timer     timer,
		float         deltaTime)
	{
		if (timer.State != TimerState.Running || deltaTime <= 0f)
			return;
		if (timer.DurationSeconds <= 0f || float.IsNaN(timer.DurationSeconds) || float.IsInfinity(timer.DurationSeconds) ||
			float.IsNaN(timer.ElapsedSeconds) || float.IsInfinity(timer.ElapsedSeconds))
			return;

		timer.ElapsedSeconds += deltaTime;
		if (timer.ElapsedSeconds < timer.DurationSeconds)
			return;

		timer.ElapsedSeconds = timer.DurationSeconds;
		timer.State = TimerState.Completed;

		Publish(world, timerEntity, timer, TimerLifecycle.Finished);

		if (timer.Mode == TimerMode.OneShot)
			commands.Destroy(timerEntity);
	}

	private void PublishPendingTransitions(World world)
	{
		if (!world.TryReadResource<TimerTransitionQueue>(out var pending)) return;
		// Callback-enqueued transitions wait for the next update, bounding reentrant work.
		int count = pending.Count;
		for (int i = 0; i < count && pending.TryDequeue(out var transition); i++)
		{
			if (!world.IsAlive(transition.TimerEntity) || !world.Has<Timer>(transition.TimerEntity)) continue;
			Publish(world, in transition);
		}
	}

	private void Publish(
		World         world,
		Entity        timerEntity,
		in Timer      timer,
		TimerLifecycle lifecycle)
	{
		var ownerEntity = TryResolveOwner(world, timerEntity);
		var eventData = new TimerLifecycleEvent(
			timerEntity,
			ownerEntity,
			timer.TimerId,
			lifecycle,
			timer.State,
			timer.ElapsedSeconds,
			timer.DurationSeconds
		);

		Publish(world, in eventData);
	}

	private void Publish(World world, in TimerLifecycleEvent eventData)
	{
		ref var events = ref world.GetOrCreateResource<TimerEventsResource>();
		events.Enqueue(in eventData);

		try
		{
			GetHandler(eventData.Lifecycle)?.Invoke(eventData);
		}
		catch
		{
			// Event handler exceptions should not break simulation.
		}
	}

	private Action<TimerLifecycleEvent>? GetHandler(TimerLifecycle lifecycle) =>
		lifecycle switch
		{
			TimerLifecycle.Started => Started,
			TimerLifecycle.Paused => Paused,
			TimerLifecycle.Stopped => Stopped,
			TimerLifecycle.Finished => Finished,
			TimerLifecycle.Resumed => Resumed,
			TimerLifecycle.Restarted => Restarted,
			_ => null
		};

	private static Entity TryResolveOwner(World world, Entity timerEntity)
	{
		if (world.TryRead(timerEntity, out TimerOwner owner))
			return owner.OwnerEntity;

		return Entity.None;
	}
}

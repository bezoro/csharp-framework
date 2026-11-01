using System.Collections.Generic;
using Bezoro.ECS.Services;
using Bezoro.GameSystems.TimerSystem.Extensions;
using Bezoro.GameSystems.TimerSystem.Types;
using Xunit;
using Timer = Bezoro.GameSystems.TimerSystem.Types.Timer;

namespace Bezoro.GameSystems.Tests.TimerSystem;

public sealed class TimerTransitionOrderingTests
{
	[Fact]
	public void CallbackControls_WaitForNextDrainAndApplyBeforeAdvancement()
	{
		using var world = new World();
		var system = new Bezoro.GameSystems.TimerSystem.Services.TimerSystem();
		world.AddSystem(system);
		var timer = world.Spawn(new Timer(1, 10f));
		system.Started += _ => Assert.True(world.PauseTimer(timer));
		world.StartTimer(timer);
		world.Tick(1f);
		var events = world.ReadResource<TimerEventsResource>();
		Assert.Equal(1, events.Count);
		Assert.True(events.TryDequeue(out var started));
		Assert.Equal(TimerState.Running, started.State);
		Assert.Equal(TimerState.Paused, world.Read<Timer>(timer).State);
		Assert.Equal(0f, world.Read<Timer>(timer).ElapsedSeconds);
		world.Tick(1f);
		Assert.True(events.TryDequeue(out var paused));
		Assert.Equal(TimerLifecycle.Paused, paused.Lifecycle);
		Assert.Equal(0, events.Count);
	}

	[Fact]
	public void DestroyedTimer_DiscardsItsPendingControls()
	{
		using var world = new World();
		world.AddSystem(new Bezoro.GameSystems.TimerSystem.Services.TimerSystem());
		var timer = world.Spawn(new Timer(1, 10f));
		world.StartTimer(timer);
		world.Despawn(timer);
		world.Tick(0f);
		Assert.Equal(0, world.ReadResource<TimerEventsResource>().Count);
	}

	[Fact]
	public void ControlsBeforeTick_PreserveEveryAcceptedTransitionAndItsState()
	{
		using var world = new World();
		world.AddSystem(new Bezoro.GameSystems.TimerSystem.Services.TimerSystem());
		var timer = world.Spawn(new Timer(1, 10f));
		Assert.True(world.StartTimer(timer));
		Assert.True(world.PauseTimer(timer));
		Assert.True(world.ResumeTimer(timer));
		Assert.True(world.StopTimer(timer));
		world.Tick(1f);
		var events = new List<TimerLifecycleEvent>();
		while (world.ReadResource<TimerEventsResource>().TryDequeue(out var transition)) events.Add(transition);
		Assert.Equal(new[] { TimerLifecycle.Started, TimerLifecycle.Paused, TimerLifecycle.Resumed, TimerLifecycle.Stopped }, events.ConvertAll(e => e.Lifecycle));
		Assert.Equal(new[] { TimerState.Running, TimerState.Paused, TimerState.Running, TimerState.Stopped }, events.ConvertAll(e => e.State));
		Assert.All(events, e => Assert.Equal(0f, e.ElapsedSeconds));
	}
}

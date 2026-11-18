using System.Collections.Generic;

namespace Bezoro.GameSystems.TimerSystem.Types;

// Created on the first accepted control operation, never once per timer.
internal sealed class TimerTransitionQueue
{
	private readonly Queue<TimerLifecycleEvent> _transitions = new();
	internal int Count => _transitions.Count;
	internal void Enqueue(in TimerLifecycleEvent transition) => _transitions.Enqueue(transition);
	internal bool TryPeek(out TimerLifecycleEvent transition) => _transitions.TryPeek(out transition);
	internal void Dequeue() => _transitions.Dequeue();
}

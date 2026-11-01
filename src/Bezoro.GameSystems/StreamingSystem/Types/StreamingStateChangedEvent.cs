using Bezoro.ECS.Types;

namespace Bezoro.GameSystems.StreamingSystem.Types;

/// <summary>
///     Event payload describing a streaming state transition.
/// </summary>
/// <param name="TargetEntity">Entity that transitioned.</param>
/// <param name="Transition">Transition kind.</param>
/// <param name="DistanceSquared">Squared distance to reference point at transition time.</param>
public readonly record struct StreamingStateChangedEvent(
	Entity              TargetEntity,
	StreamingTransition Transition,
	double              DistanceSquared
)
{
	/// <summary>
	///     Gets whether the transition result is streamed-in.
	/// </summary>
	public bool IsStreamedIn => Transition == StreamingTransition.StreamedIn;
}

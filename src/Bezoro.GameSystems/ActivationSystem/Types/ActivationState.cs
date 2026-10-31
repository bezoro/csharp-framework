namespace Bezoro.GameSystems.ActivationSystem.Types;

/// <summary>
///     Represents the current state of an activation entry.
/// </summary>
public enum ActivationState : byte
{
	/// <summary>The entry is waiting to be activated.</summary>
	Pending,

	/// <summary>The entry was selected and its callback queued for dispatch. Host dispatch can defer execution.</summary>
	Activated,

	/// <summary>The entry was cancelled before activation.</summary>
	Cancelled
}

using System;

namespace Bezoro.GameSystems.ActivationSystem.Types;

/// <summary>
///     ECS component representing an activation callback and its processing state.
/// </summary>
public struct ActivationEntry
{
	private Action? _callback;
	/// <summary>
	///     Initializes a new activation entry.
	/// </summary>
	/// <param name="handle">Stable handle assigned by the command queue.</param>
	/// <param name="callback">Callback invoked once the entry is activated.</param>
	/// <param name="priority">Activation priority. Higher values activate first.</param>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="callback" /> is null.</exception>
	public ActivationEntry(
		ActivationHandle handle,
		Action           callback,
		int              priority = 0)
	{
		if (callback is null) throw new ArgumentNullException(nameof(callback));
		if (!handle.IsValid) throw new ArgumentException("Activation handle must be valid.", nameof(handle));

		Handle = handle;
		_callback = callback;
		Priority = priority;
		State = ActivationState.Pending;
	}

	/// <summary>
	///     Gets the stable activation handle for this entry.
	/// </summary>
	public ActivationHandle Handle { get; }

	/// <summary>
	///     Gets priority used for ordering pending entries.
	/// </summary>
	public int Priority { get; }

	/// <summary>
	///     Gets the current activation state. Only the activation pipeline changes it.
	/// </summary>
	public ActivationState State { get; private set; }

	internal bool TryActivate(out Action callback)
	{
		callback = null!;
		if (State != ActivationState.Pending || _callback is null) return false;
		callback = _callback;
		_callback = null;
		State = ActivationState.Activated;
		return true;
	}

	internal void CancelPending()
	{
		if (State != ActivationState.Pending) return;
		_callback = null;
		State = ActivationState.Cancelled;
	}
}

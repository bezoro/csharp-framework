using System;

namespace Bezoro.GameSystems.TimerSystem.Types;

/// <summary>
///     Timer component processed by <see cref="Services.TimerSystem" />.
/// </summary>
public struct Timer
{
	/// <summary>
	///     Initializes a new timer component.
	/// </summary>
	/// <param name="timerId">Application-level identifier for this timer.</param>
	/// <param name="durationSeconds">Total timer duration in seconds.</param>
	/// <param name="elapsedSeconds">Initial elapsed time in seconds.</param>
	/// <param name="state">Initial runtime state.</param>
	/// <param name="mode">Lifecycle behavior after completion.</param>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when duration is not positive and finite, or elapsed time is not finite.</exception>
	public Timer(
		int        timerId,
		float      durationSeconds,
		float      elapsedSeconds = 0f,
		TimerState state          = TimerState.Stopped,
		TimerMode  mode           = TimerMode.Persistent)
	{
		if (durationSeconds <= 0f || float.IsNaN(durationSeconds) || float.IsInfinity(durationSeconds))
			throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Duration must be positive and finite.");
		if (float.IsNaN(elapsedSeconds) || float.IsInfinity(elapsedSeconds))
			throw new ArgumentOutOfRangeException(nameof(elapsedSeconds), "Elapsed time must be finite.");
		if (state < TimerState.Running || state > TimerState.Completed)
			throw new ArgumentOutOfRangeException(nameof(state));
		if (mode is not (TimerMode.OneShot or TimerMode.Persistent))
			throw new ArgumentOutOfRangeException(nameof(mode));

		TimerId = timerId;
		DurationSeconds = durationSeconds;
		ElapsedSeconds = Math.Clamp(elapsedSeconds, 0f, durationSeconds);
		State = state;
		Mode = mode;
	}

	/// <summary>
	///     Gets or sets the timer id used by gameplay systems to correlate timer entities.
	/// </summary>
	public int TimerId;

	/// <summary>
	///     Gets or sets the configured duration in seconds.
	/// </summary>
	public float DurationSeconds;

	/// <summary>
	///     Gets or sets the current elapsed time in seconds.
	/// </summary>
	public float ElapsedSeconds;

	/// <summary>
	///     Gets or sets the runtime state.
	/// </summary>
	public TimerState State;

	/// <summary>
	///     Gets or sets completion behavior for this timer.
	/// </summary>
	public TimerMode Mode;

	/// <summary>
	///     Gets the remaining time in seconds clamped to zero.
	/// </summary>
	public readonly float RemainingSeconds => Math.Max(0f, DurationSeconds - ElapsedSeconds);

	/// <summary>
	///     Gets completion progress from 0 to 1.
	/// </summary>
	public readonly float Progress => DurationSeconds <= 0f ? 1f : Math.Min(1f, ElapsedSeconds / DurationSeconds);
}

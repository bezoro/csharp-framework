namespace Bezoro.Chess.UCI.Protocol.API.Types;

/// <summary>
///     Describes clock values to apply while loading an authored or saved playable match.
/// </summary>
public readonly record struct PlayableMatchClockSetup
{
	/// <summary>
	///     Creates a clock setup whose active side, completed move counts, and active stage are derived from the loaded
	///     match position.
	/// </summary>
	/// <param name="whiteRemaining">White's remaining time at <paramref name="snapshotUtc" />.</param>
	/// <param name="blackRemaining">Black's remaining time at <paramref name="snapshotUtc" />.</param>
	/// <param name="delayRemaining">Remaining delay before the active side's main clock decreases.</param>
	/// <param name="isPaused">Whether the clock should be restored in a paused state.</param>
	/// <param name="snapshotUtc">Timestamp represented by this clock setup. Uses the current UTC time when omitted.</param>
	public PlayableMatchClockSetup(
		TimeSpan        whiteRemaining,
		TimeSpan        blackRemaining,
		TimeSpan        delayRemaining = default,
		bool            isPaused       = true,
		DateTimeOffset? snapshotUtc    = null)
	{
		WhiteRemaining = whiteRemaining;
		BlackRemaining = blackRemaining;
		DelayRemaining = delayRemaining;
		IsPaused       = isPaused;
		SnapshotUtc    = snapshotUtc;
		ExactRestore   = null;
	}

	private PlayableMatchClockSetup(PlayableMatchClockRestore exactRestore)
	{
		WhiteRemaining = exactRestore.WhiteRemaining;
		BlackRemaining = exactRestore.BlackRemaining;
		DelayRemaining = exactRestore.DelayRemaining;
		IsPaused       = exactRestore.IsPaused;
		SnapshotUtc    = exactRestore.SnapshotUtc;
		ExactRestore   = exactRestore;
	}

	/// <summary>
	///     Gets White's remaining time at <see cref="SnapshotUtc" />.
	/// </summary>
	public TimeSpan WhiteRemaining { get; }

	/// <summary>
	///     Gets Black's remaining time at <see cref="SnapshotUtc" />.
	/// </summary>
	public TimeSpan BlackRemaining { get; }

	/// <summary>
	///     Gets the remaining delay before the active side's main clock decreases.
	/// </summary>
	public TimeSpan DelayRemaining { get; }

	/// <summary>
	///     Gets whether the clock should be restored in a paused state.
	/// </summary>
	public bool IsPaused { get; }

	/// <summary>
	///     Gets the timestamp represented by this clock setup. A missing value means the load time should be used.
	/// </summary>
	public DateTimeOffset? SnapshotUtc { get; }

	/// <summary>
	///     Gets the exact advanced restore snapshot, when this setup was created with <see cref="FromExactRestore" />.
	/// </summary>
	public PlayableMatchClockRestore? ExactRestore { get; }

	/// <summary>Creates a valid clock snapshot from authored seconds, clamping negative values to zero.</summary>
	/// <remarks>Negative infinity is clamped to zero. NaN and positive infinity are rejected by <see cref="TimeSpan.FromSeconds(double)" />.</remarks>
	/// <exception cref="ArgumentException">A value is NaN.</exception>
	/// <exception cref="OverflowException">A positive value exceeds the range of <see cref="TimeSpan" />.</exception>
	public static PlayableMatchClockSetup FromClampedSeconds(
		double whiteRemainingSeconds, double blackRemainingSeconds, double delayRemainingSeconds = 0,
		bool isPaused = true, DateTimeOffset? snapshotUtc = null)
	{
		var setup = new PlayableMatchClockSetup(
			TimeSpan.FromSeconds(Math.Max(0, whiteRemainingSeconds)),
			TimeSpan.FromSeconds(Math.Max(0, blackRemainingSeconds)),
			TimeSpan.FromSeconds(Math.Max(0, delayRemainingSeconds)), isPaused, snapshotUtc);
		setup.Validate();
		return setup;
	}

	/// <summary>
	///     Creates a clock setup from an advanced exact snapshot that supplies active side, move counts, and stage.
	/// </summary>
	/// <param name="restore">Exact clock snapshot to restore.</param>
	/// <returns>A clock setup that preserves all supplied restore details.</returns>
	public static PlayableMatchClockSetup FromExactRestore(PlayableMatchClockRestore restore)
	{
		restore.Validate();
		return new(restore);
	}

	/// <summary>
	///     Validates that the setup contains non-negative time values and, when present, a valid exact restore snapshot.
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when any time value is outside its valid range.</exception>
	public void Validate()
	{
		if (ExactRestore.HasValue)
		{
			ExactRestore.Value.Validate();
			return;
		}

		if (WhiteRemaining < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(WhiteRemaining), "White remaining time cannot be negative.");

		if (BlackRemaining < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(BlackRemaining), "Black remaining time cannot be negative.");

		if (DelayRemaining < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(DelayRemaining), "Delay remaining cannot be negative.");
	}
}

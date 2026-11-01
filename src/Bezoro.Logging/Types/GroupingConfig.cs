namespace Bezoro.Logging.Types;

/// <summary>
///     Configuration for log grouping.
/// </summary>
public readonly struct GroupingConfig
{
	private GroupingConfig(LoggerSettings.ContextGrouping groupBy, bool includeInOutput, int timeWindowMs)
	{
		GroupBy = groupBy;
		IncludeInOutput = includeInOutput;
		TimeWindowMs = timeWindowMs;
	}

	/// <summary>
	///     Creates a grouping configuration with the specified strategy.
	/// </summary>
	/// <param name="groupBy">The grouping strategy.</param>
	/// <param name="includeInOutput">Whether to include grouping info in the formatted message.</param>
	/// <param name="timeWindowMs">Time window for TimeWindow grouping.</param>
	public static GroupingConfig Create(
		LoggerSettings.ContextGrouping groupBy,
		bool                           includeInOutput = true,
		int                            timeWindowMs    = 100)
	{
		if (groupBy < LoggerSettings.ContextGrouping.None || groupBy > LoggerSettings.ContextGrouping.AsyncContext)
			throw new ArgumentOutOfRangeException(nameof(groupBy));
		if (groupBy == LoggerSettings.ContextGrouping.TimeWindow && timeWindowMs <= 0)
			throw new ArgumentOutOfRangeException(nameof(timeWindowMs), "Time-window grouping requires a positive duration.");
		return new(groupBy, includeInOutput, timeWindowMs);
	}

	/// <summary>
	///     No grouping.
	/// </summary>
	public static GroupingConfig None => default;

	/// <summary>
	///     Whether to include grouping context in the formatted output.
	/// </summary>
	public bool IncludeInOutput { get; }

	/// <summary>
	///     The grouping strategy to use.
	/// </summary>
	public LoggerSettings.ContextGrouping GroupBy { get; }

	/// <summary>
	///     Time window in milliseconds for TimeWindow grouping (only used when GroupBy is TimeWindow).
	/// </summary>
	public int TimeWindowMs { get; }
}

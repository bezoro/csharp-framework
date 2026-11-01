namespace Bezoro.Logging.Types;

/// <summary>
///     Contains all data for a single log event.
/// </summary>
public sealed class LogPayload
{
	/// <summary>Creates a payload with its required display values, callable from C# 9 consumers.</summary>
	/// <param name="level">Defined severity or divider level.</param>
	/// <param name="message">Unformatted message text.</param>
	/// <param name="formattedMessage">Message ready for the destination sink.</param>
	/// <param name="severityEmoji">Display marker for the level.</param>
	public LogPayload(LogLevel level, string message, string formattedMessage, string severityEmoji)
	{
		if (level < LogLevel.None || level > LogLevel.Divider) throw new ArgumentOutOfRangeException(nameof(level));
		Level = level;
		Message = message ?? throw new ArgumentNullException(nameof(message));
		FormattedMessage = formattedMessage ?? throw new ArgumentNullException(nameof(formattedMessage));
		SeverityEmoji = severityEmoji ?? throw new ArgumentNullException(nameof(severityEmoji));
	}

	/// <summary>
	///     UTC timestamp when the log was created.
	/// </summary>
	public DateTime Timestamp { get; init; } = DateTime.UtcNow;

	/// <summary>
	///     Exception instance (only for exceptions).
	/// </summary>
	public Exception? Exception { get; init; }

	/// <summary>
	///     Async context nesting depth (0 = no context, 1 = root, 2+ = nested).
	/// </summary>
	public int AsyncContextDepth { get; init; }

	/// <summary>
	///     Async context hierarchy (e.g., ["GameLoop", "Player-123", "AI"]).
	/// </summary>
	public IReadOnlyList<string>? AsyncContextHierarchy { get; init; }

	/// <summary>
	///     Optional category for the log message.
	/// </summary>
	public LogCategory? Category { get; init; }

	/// <summary>
	///     The severity level of the log.
	/// </summary>
	public LogLevel Level { get; }

	/// <summary>
	///     Visual style hints for rendering (color, bold, etc.).
	/// </summary>
	public LogStyle Style { get; init; } = LoggerSettings.InfoStyle;

	/// <summary>
	///     Optional context object (e.g., Unity Object for console highlighting).
	/// </summary>
	public object? ContextObject { get; init; }

	/// <summary>
	///     Fully formatted log message ready for output.
	/// </summary>
	public string FormattedMessage { get; }

	/// <summary>
	///     The raw message content.
	/// </summary>
	public string Message { get; }

	/// <summary>
	///     Emoji representing the severity level.
	/// </summary>
	public string SeverityEmoji { get; }

	/// <summary>
	///     Async context hierarchy joined with <c> &gt; </c> separators.
	/// </summary>
	public string? AsyncContext { get; init; }

	/// <summary>
	///     Caller information in format "TypeName.MethodName()" (if captured).
	/// </summary>
	public string? CallerInfo { get; init; }

	/// <summary>
	///     Emoji representing the category (if category is specified).
	/// </summary>
	public string? CategoryEmoji { get; init; }

	/// <summary>
	///     Exception type name (only for exceptions).
	/// </summary>
	public string? ExceptionType { get; init; }

	/// <summary>
	///     Automatic grouping context computed from <see cref="LoggerSettings.Grouping" />.
	/// </summary>
	public string? GroupingContext { get; init; }

	/// <summary>
	///     Inner exception message (if present).
	/// </summary>
	public string? InnerExceptionMessage { get; init; }

	/// <summary>
	///     Inner exception type name (if present).
	/// </summary>
	public string? InnerExceptionType { get; init; }

	/// <summary>
	///     Full stack trace (only for exceptions).
	/// </summary>
	public string? StackTrace { get; init; }

	/// <summary>
	///     Optional stage label included in the formatted output.
	/// </summary>
	public string? Stage { get; init; }
}

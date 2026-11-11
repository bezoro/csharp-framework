using Bezoro.Logging.Internal;

namespace Bezoro.Logging.Types;

/// <summary>
///     Configuration settings for the Logger.
/// </summary>
public static class LoggerSettings
{
	/// <summary>
	///     Log sequence counter for tracking log order.
	/// </summary>
	private static long _sequenceNumber;

	private static readonly object ConfigurationGate = new();
	private static LogConfiguration _configuration = new();
	internal static LogConfiguration CaptureConfiguration() => Volatile.Read(ref _configuration);

	/// <summary>Thread-safe muted categories, with snapshot enumeration and copy-on-write changes.</summary>
	public static ISet<LogCategory> MutedCategories { get; } = new ConcurrentCategorySet();

	/// <summary>
	///     Master switch to enable/disable all logging at runtime.
	/// </summary>
	public static bool Enabled
	{
		get => CaptureConfiguration().Enabled;
		set { lock (ConfigurationGate) Volatile.Write(ref _configuration, _configuration with { Enabled = value }); }
	}

	/// <summary>
	///     File location metadata configuration.
	/// </summary>
	public static FileLocationConfig FileLocation
	{
		get => CaptureConfiguration().FileLocation;
		set { lock (ConfigurationGate) Volatile.Write(ref _configuration, _configuration with { FileLocation = value }); }
	}

	/// <summary>
	///     Frame count metadata configuration.
	/// </summary>
	public static FrameCountConfig FrameCount
	{
		get => CaptureConfiguration().FrameCount;
		set { lock (ConfigurationGate) Volatile.Write(ref _configuration, _configuration with { FrameCount = value }); }
	}

	/// <summary>
	///     Grouping configuration.
	/// </summary>
	public static GroupingConfig Grouping
	{
		get => CaptureConfiguration().Grouping;
		set { lock (ConfigurationGate) Volatile.Write(ref _configuration, _configuration with { Grouping = value }); }
	}

	/// <summary>
	///     Visual style for Error level logs.
	/// </summary>
	public static LogStyle ErrorStyle
	{
		get => CaptureConfiguration().ErrorStyle;
		set { lock (ConfigurationGate) Volatile.Write(ref _configuration, _configuration with { ErrorStyle = value }); }
	}

	/// <summary>
	///     Visual style for Exception level logs.
	/// </summary>
	public static LogStyle ExceptionStyle
	{
		get => CaptureConfiguration().ExceptionStyle;
		set { lock (ConfigurationGate) Volatile.Write(ref _configuration, _configuration with { ExceptionStyle = value }); }
	}

	/// <summary>
	///     Visual style for Info level logs.
	/// </summary>
	public static LogStyle InfoStyle
	{
		get => CaptureConfiguration().InfoStyle;
		set { lock (ConfigurationGate) Volatile.Write(ref _configuration, _configuration with { InfoStyle = value }); }
	}

	/// <summary>
	///     Visual style for Success level logs.
	/// </summary>
	public static LogStyle SuccessStyle
	{
		get => CaptureConfiguration().SuccessStyle;
		set { lock (ConfigurationGate) Volatile.Write(ref _configuration, _configuration with { SuccessStyle = value }); }
	}

	/// <summary>
	///     Visual style for Warning level logs.
	/// </summary>
	public static LogStyle WarningStyle
	{
		get => CaptureConfiguration().WarningStyle;
		set { lock (ConfigurationGate) Volatile.Write(ref _configuration, _configuration with { WarningStyle = value }); }
	}

	/// <summary>
	///     Sequence number metadata configuration.
	/// </summary>
	public static SequenceNumberConfig SequenceNumber
	{
		get => CaptureConfiguration().SequenceNumber;
		set { lock (ConfigurationGate) Volatile.Write(ref _configuration, _configuration with { SequenceNumber = value }); }
	}

	/// <summary>
	///     Stage metadata configuration.
	/// </summary>
	public static StageConfig Stage
	{
		get => CaptureConfiguration().Stage;
		set { lock (ConfigurationGate) Volatile.Write(ref _configuration, _configuration with { Stage = value }); }
	}

	/// <summary>
	///     Thread ID metadata configuration.
	/// </summary>
	public static ThreadIdConfig ThreadId
	{
		get => CaptureConfiguration().ThreadId;
		set { lock (ConfigurationGate) Volatile.Write(ref _configuration, _configuration with { ThreadId = value }); }
	}

	/// <summary>
	///     Timestamp metadata configuration.
	/// </summary>
	public static TimestampConfig Timestamp
	{
		get => CaptureConfiguration().Timestamp;
		set { lock (ConfigurationGate) Volatile.Write(ref _configuration, _configuration with { Timestamp = value }); }
	}

	/// <summary>
	///     Gets the current async context hierarchy.
	/// </summary>
	internal static IReadOnlyList<string>? CurrentAsyncContextHierarchy => AsyncContextScope.CurrentHierarchy;

	/// <summary>
	///     Begins a new async context that automatically flows through async/await.
	/// </summary>
	/// <param name="contextName">The name for this async context.</param>
	/// <returns>A disposable that restores the previous context when disposed.</returns>
	public static IDisposable BeginAsyncContext(string contextName) => new AsyncContextScope(contextName);

	/// <summary>
	///     Gets the style for a specific log level.
	/// </summary>
	internal static LogStyle GetStyle(LogLevel level) => level switch
	{
		LogLevel.Info      => InfoStyle,
		LogLevel.Success   => SuccessStyle,
		LogLevel.Warning   => WarningStyle,
		LogLevel.Error     => ErrorStyle,
		LogLevel.Exception => ExceptionStyle,
		_                  => InfoStyle
	};

	/// <summary>
	///     Gets the next sequence number for log ordering.
	/// </summary>
	internal static long GetNextSequenceNumber() => Interlocked.Increment(ref _sequenceNumber);


	/// <summary>
	///     Defines how logs should be grouped/contextualized.
	/// </summary>
	public enum ContextGrouping
	{
		/// <summary>No automatic grouping.</summary>
		None,

		/// <summary>Group by calling class name.</summary>
		CallerType,

		/// <summary>Group by calling method name (ClassName.MethodName).</summary>
		CallerMethod,

		/// <summary>Group by log category.</summary>
		Category,

		/// <summary>Group by thread ID.</summary>
		Thread,

		/// <summary>Group by log severity level.</summary>
		Level,

		/// <summary>Group by time window (logs within TimeWindowMs are grouped together).</summary>
		TimeWindow,

		/// <summary>Group by async context (automatically flows through async/await).</summary>
		AsyncContext
	}
}

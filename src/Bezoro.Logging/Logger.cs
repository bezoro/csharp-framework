using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Bezoro.Logging.Internal;
using Bezoro.Logging.Types;
using Bezoro.Logging.Utilities;

namespace Bezoro.Logging;

/// <summary>
///     Provides a clean, flexible logging utility with optional complexity.
/// </summary>
public static class Logger
{
	private static string? _lastStage;

	/// <summary>
	///     Event invoked when a log message is processed.
	/// </summary>
	public static event Action<LogPayload>? OnLog;

	/// <summary>
	///     Minimum log level to process. Logs below this level are ignored.
	///     Default: <see cref="LogLevel.Info" />.
	/// </summary>
	public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

	/// <summary>Reports whether a listener would receive this level and category under the current filters.</summary>
	/// <remarks>Use before constructing expensive arguments. This is a snapshot; listeners and settings can change.</remarks>
	public static bool IsEnabled(LogLevel level = LogLevel.Info, LogCategory? category = null) => !ShouldSkipLog(level, category);

	/// <summary>
	///     Begins a performance timer that logs the operation duration when disposed if the
	///     Bezoro.Logging assembly is compiled with <c>DEBUG</c>.
	/// </summary>
	/// <param name="operationName">The name of the operation being timed.</param>
	/// <param name="category">Optional log category.</param>
	/// <param name="contextObject">Optional context object.</param>
	/// <returns>
	///     A scoped timer that records and logs elapsed time when the Bezoro.Logging assembly is compiled with <c>DEBUG</c>;
	///     otherwise, its disposal performs no work.
	/// </returns>
	/// <example>
	///     <code>
	/// using (Logger.BeginTimer("LoadLevel", LogCategory.Loading))
	/// {
	///     // ... operation code ...
	/// }
	/// // Output includes: ℹ️ [⏳] LoadLevel completed in 123.45ms
	/// </code>
	/// </example>
	public static PerformanceTimer BeginTimer(
		string       operationName,
		LogCategory? category      = null,
		object?      contextObject = null) =>
		new(operationName, category, contextObject);

	/// <summary>
	///     Logs a message with optional complexity.
	/// </summary>
	/// <param name="message">The message to log (string, number, object, FormattableString, etc.).</param>
	/// <param name="level">The severity level (default: Info).</param>
	/// <param name="category">Optional log category.</param>
	/// <param name="contextObject">Optional context object (e.g., Unity Object for console highlighting).</param>
	/// <param name="captureCallerInfo">Whether to automatically capture caller information.</param>
	/// <param name="memberName">
	///     Automatically populated with the calling member name via <see cref="CallerMemberNameAttribute" />.
	///     Do not provide this parameter manually.
	/// </param>
	/// <param name="filePath">
	///     Automatically populated with the source file path via <see cref="CallerFilePathAttribute" />.
	///     Do not provide this parameter manually.
	/// </param>
	[Conditional("DEBUG")]
	public static void Log(
		object?                    message,
		LogLevel                   level             = LogLevel.Info,
		LogCategory?               category          = null,
		object?                    contextObject     = null,
		bool                       captureCallerInfo = false,
		[CallerMemberName] string? memberName        = null,
		[CallerFilePath]   string? filePath          = null) =>
		LogInternal(message, level, category, contextObject, captureCallerInfo, memberName, filePath);

	private static void LogInternal(
		object?      message,
		LogLevel     level,
		LogCategory? category,
		object?      contextObject,
		bool         captureCallerInfo,
		string?      memberName,
		string?      filePath)
	{
		if (ShouldSkipLog(level, category)) return;

		// Capture caller info if requested
		string? callerInfo = null;
		if (captureCallerInfo && memberName != null)
		{
			string typeName = ExtractTypeNameFromFilePath(filePath);
			callerInfo = $"{typeName}.{memberName}()";
		}

		// Format the message based on type
		string formattedMessage = message is FormattableString formattable
									  ? FormatMessage(formattable)
									  : FormatMessage(message);

		BuildAndInvokePayload(new LogInput(
			formattedMessage,
			level,
			category,
			contextObject,
			null,
			null,
			callerInfo,
			null,
			null,
			null,
			filePath));
	}

	/// <summary>
	///     Logs an exception with automatic detail extraction.
	/// </summary>
	/// <param name="exception">The exception to log.</param>
	/// <param name="customMessage">Optional custom message to prepend to the exception message for additional context.</param>
	/// <param name="category">Optional log category.</param>
	/// <param name="contextObject">Optional context object (e.g., Unity Object for console highlighting).</param>
	/// <param name="captureCallerInfo">Whether to automatically capture caller information (default: true).</param>
	/// <param name="memberName">
	///     Automatically populated with the calling member name via <see cref="CallerMemberNameAttribute" />.
	///     Do not provide this parameter manually.
	/// </param>
	/// <param name="filePath">
	///     Automatically populated with the source file path via <see cref="CallerFilePathAttribute" />.
	///     Do not provide this parameter manually.
	/// </param>
	[Conditional("DEBUG")]
	public static void Log(
		Exception                  exception,
		string?                    customMessage     = null,
		LogCategory?               category          = null,
		object?                    contextObject     = null,
		bool                       captureCallerInfo = true,
		[CallerMemberName] string? memberName        = null,
		[CallerFilePath]   string? filePath          = null)
	{
		LogExceptionInternal(
			exception,
			LogLevel.Exception,
			customMessage,
			category,
			contextObject,
			captureCallerInfo,
			memberName,
			filePath
		);
	}


	/// <summary>
	///     Logs an error message with optional complexity.
	/// </summary>
	/// <param name="message">The message to log (string, number, object, FormattableString, etc.).</param>
	/// <param name="category">Optional log category.</param>
	/// <param name="contextObject">Optional context object (e.g., Unity Object for console highlighting).</param>
	/// <param name="captureCallerInfo">Whether to automatically capture caller information.</param>
	/// <param name="memberName">
	///     Automatically populated with the calling member name via <see cref="CallerMemberNameAttribute" />.
	///     Do not provide this parameter manually.
	/// </param>
	/// <param name="filePath">
	///     Automatically populated with the source file path via <see cref="CallerFilePathAttribute" />.
	///     Do not provide this parameter manually.
	/// </param>
	[Conditional("DEBUG")]
	public static void LogError(
		object                     message,
		LogCategory?               category          = null,
		object?                    contextObject     = null,
		bool                       captureCallerInfo = false,
		[CallerMemberName] string? memberName        = null,
		[CallerFilePath]   string? filePath          = null) =>
		LogInternal(message, LogLevel.Error, category, contextObject, captureCallerInfo, memberName, filePath);

	/// <summary>
	///     Logs an exception with automatic detail extraction.
	/// </summary>
	/// <param name="exception">The exception to log.</param>
	/// <param name="customMessage">Optional custom message to prepend to the exception message for additional context.</param>
	/// <param name="category">Optional log category.</param>
	/// <param name="contextObject">Optional context object (e.g., Unity Object for console highlighting).</param>
	/// <param name="captureCallerInfo">Whether to automatically capture caller information (default: true).</param>
	/// <param name="memberName">
	///     Automatically populated with the calling member name via <see cref="CallerMemberNameAttribute" />.
	///     Do not provide this parameter manually.
	/// </param>
	/// <param name="filePath">
	///     Automatically populated with the source file path via <see cref="CallerFilePathAttribute" />.
	///     Do not provide this parameter manually.
	/// </param>
	[Conditional("DEBUG")]
	public static void LogException(
		Exception                  exception,
		string?                    customMessage     = null,
		LogCategory?               category          = null,
		object?                    contextObject     = null,
		bool                       captureCallerInfo = true,
		[CallerMemberName] string? memberName        = null,
		[CallerFilePath]   string? filePath          = null) =>
		LogExceptionInternal(
			exception,
			LogLevel.Exception,
			customMessage,
			category,
			contextObject,
			captureCallerInfo,
			memberName,
			filePath
		);


	/// <summary>
	///     Logs a success message with optional complexity.
	/// </summary>
	/// <param name="message">The message to log (string, number, object, FormattableString, etc.).</param>
	/// <param name="category">Optional log category.</param>
	/// <param name="contextObject">Optional context object (e.g., Unity Object for console highlighting).</param>
	/// <param name="captureCallerInfo">Whether to automatically capture caller information.</param>
	/// <param name="memberName">
	///     Automatically populated with the calling member name via <see cref="CallerMemberNameAttribute" />.
	///     Do not provide this parameter manually.
	/// </param>
	/// <param name="filePath">
	///     Automatically populated with the source file path via <see cref="CallerFilePathAttribute" />.
	///     Do not provide this parameter manually.
	/// </param>
	[Conditional("DEBUG")]
	public static void LogSuccess(
		object                     message,
		LogCategory?               category          = null,
		object?                    contextObject     = null,
		bool                       captureCallerInfo = false,
		[CallerMemberName] string? memberName        = null,
		[CallerFilePath]   string? filePath          = null) =>
		LogInternal(message, LogLevel.Success, category, contextObject, captureCallerInfo, memberName, filePath);

	/// <summary>
	///     Logs a warning message with optional complexity.
	/// </summary>
	/// <param name="message">The message to log (string, number, object, FormattableString, etc.).</param>
	/// <param name="category">Optional log category.</param>
	/// <param name="contextObject">Optional context object (e.g., Unity Object for console highlighting).</param>
	/// <param name="captureCallerInfo">Whether to automatically capture caller information.</param>
	/// <param name="memberName">
	///     Automatically populated with the calling member name via <see cref="CallerMemberNameAttribute" />.
	///     Do not provide this parameter manually.
	/// </param>
	/// <param name="filePath">
	///     Automatically populated with the source file path via <see cref="CallerFilePathAttribute" />.
	///     Do not provide this parameter manually.
	/// </param>
	[Conditional("DEBUG")]
	public static void LogWarning(
		object                     message,
		LogCategory?               category          = null,
		object?                    contextObject     = null,
		bool                       captureCallerInfo = false,
		[CallerMemberName] string? memberName        = null,
		[CallerFilePath]   string? filePath          = null) =>
		LogInternal(message, LogLevel.Warning, category, contextObject, captureCallerInfo, memberName, filePath);

	private static bool ShouldSkipLog(LogLevel level, LogCategory? category)
	{
		if (level < LogLevel.None || level > LogLevel.Divider) throw new ArgumentOutOfRangeException(nameof(level));
		if (OnLog is null || !LoggerSettings.Enabled)
			return true;

		if (level < MinimumLevel)
			return true;

		return category.HasValue && LoggerSettings.MutedCategories.Contains(category.Value);
	}

	private static bool TryUpdateStage(string? stage, out string? previousStage)
	{
		previousStage = null;

		if (stage == null)
			return false;

		string? currentStage = Volatile.Read(ref _lastStage);
		if (string.Equals(currentStage, stage, StringComparison.Ordinal))
			return false;

		Volatile.Write(ref _lastStage, stage);
		previousStage = currentStage;
		return previousStage != null;
	}

	/// <summary>
	///     Extracts the type name from a file path (e.g., "GameManager" from "path/to/GameManager.cs").
	/// </summary>
	private static string ExtractTypeNameFromFilePath(string? filePath)
	{
		if (string.IsNullOrEmpty(filePath))
			return "Unknown";

		string? fileName = Path.GetFileNameWithoutExtension(filePath);
		return fileName ?? "Unknown";
	}

	/// <summary>
	///     Formats a message object into a string representation.
	/// </summary>
	private static string FormatMessage(object? message)
	{
		if (message is not (IEnumerable collection and not string)) return message?.ToString() ?? "null";

		var collectionAsStrings =
			collection.Cast<object>().Select(o => o?.ToString() ?? "null");

		return $"[{string.Join(", ", collectionAsStrings)}]";
	}

	/// <summary>
	///     Formats a formattable string message with proper collection handling.
	/// </summary>
	private static string FormatMessage(FormattableString formattableMessage)
	{
		object?[] arguments     = formattableMessage.GetArguments();
		var       formattedArgs = new object?[arguments.Length];

		for (var i = 0; i < arguments.Length; i++)
		{
			object? arg = arguments[i];

			if (arg is IEnumerable collection and not string)
			{
				var collectionAsStrings =
					collection.Cast<object>().Select(o => o?.ToString() ?? "null");

				formattedArgs[i] = $"\n[{string.Join("\n", collectionAsStrings)}]";
			}
			else
			{
				formattedArgs[i] = arg;
			}
		}

		return string.Format(formattableMessage.Format, formattedArgs);
	}

	private static string? GetStageDividerLine(
		Func<string?, string?, string?>? dividerProvider,
		string? fromStage,
		string? toStage)
	{
		if (dividerProvider == null)
			return null;

		string? dividerLine = dividerProvider(fromStage, toStage);
		return string.IsNullOrWhiteSpace(dividerLine) ? null : dividerLine;
	}

	private static string? NormalizeStage(string? stage)
	{
		if (string.IsNullOrWhiteSpace(stage))
			return null;

		return stage.Trim();
	}

	/// <summary>
	///     Builds the log payload and invokes the OnLog event.
	/// </summary>
	private static void BuildAndInvokePayload(in LogInput input)
	{
		if (ShouldSkipLog(input.Level, input.Category)) return;

		var stageSettings = LoggerSettings.Stage;
		var stage = stageSettings.Enabled && stageSettings.Provider != null
			? stageSettings.Provider()
			: null;
		var normalizedStage = NormalizeStage(stage);
		var stageChanged = TryUpdateStage(normalizedStage, out var previousStage);
		var asyncHierarchy = LoggerSettings.CurrentAsyncContextHierarchy;

		TryEmitStageDividerPayload(
			input,
			previousStage,
			normalizedStage,
			stageChanged,
			asyncHierarchy);

		var settings = LogSettingsSnapshot.Capture();
		var timestamp = DateTime.UtcNow;
		var sequenceNumber = settings.SequenceNumber.Enabled ? LoggerSettings.GetNextSequenceNumber() : 0;
		var threadId = settings.ThreadId.Enabled || settings.Grouping.GroupBy == LoggerSettings.ContextGrouping.Thread
			? Environment.CurrentManagedThreadId : 0;
		int? frameCount = settings.FrameCount.Enabled && settings.FrameCount.Provider != null
			? settings.FrameCount.Provider()
			: null;
		var context = new LogEventContext(
			timestamp,
			sequenceNumber,
			threadId,
			frameCount,
			asyncHierarchy,
			normalizedStage);
		var payload = LogPayloadFormatter.Format(input, settings, context);

		OnLog?.Invoke(payload);
	}

	private static void LogExceptionInternal(
		Exception    exception,
		LogLevel     level,
		string?      customMessage,
		LogCategory? category,
		object?      contextObject,
		bool         captureCallerInfo,
		string?      memberName,
		string?      filePath)
	{
		if (ShouldSkipLog(level, category)) return;

		string message = string.IsNullOrEmpty(customMessage)
							 ? exception.Message
							 : $"{customMessage} | {exception.Message}";

		string  exceptionType = exception.GetType().Name;
		string? stackTrace    = exception.StackTrace;

		// Capture inner exception details
		string? innerExceptionType    = exception.InnerException?.GetType().Name;
		string? innerExceptionMessage = exception.InnerException?.Message;

		// Capture caller info if requested
		string? callerInfo = null;
		if (captureCallerInfo && memberName != null)
		{
			string typeName = ExtractTypeNameFromFilePath(filePath);
			callerInfo = $"{typeName}.{memberName}()";
		}

		BuildAndInvokePayload(new LogInput(
			message,
			level,
			category,
			contextObject,
			exception,
			exceptionType,
			callerInfo,
			stackTrace,
			innerExceptionType,
			innerExceptionMessage,
			filePath));
	}

	private static void TryEmitStageDividerPayload(
		in LogInput input,
		string?                previousStage,
		string?                stage,
		bool                   stageChanged,
		IReadOnlyList<string>? asyncHierarchy)
	{
		if (!stageChanged)
			return;

		var dividerProvider = LoggerSettings.Stage.DividerProvider;
		var dividerLine = GetStageDividerLine(dividerProvider, previousStage, stage);
		if (dividerLine == null)
			return;

		var settings = LogSettingsSnapshot.Capture();
		var context = new LogEventContext(
			DateTime.UtcNow,
			0,
			Environment.CurrentManagedThreadId,
			null,
			asyncHierarchy,
			stage);
		var payload = LogPayloadFormatter.FormatStageDivider(dividerLine, input, settings, context);
		OnLog?.Invoke(payload);
	}
}

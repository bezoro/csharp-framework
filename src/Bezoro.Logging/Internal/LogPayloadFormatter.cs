using Bezoro.Logging.Types;

namespace Bezoro.Logging.Internal;

internal static class LogPayloadFormatter
{
	internal static LogPayload Format(
		in LogInput input,
		in LogSettingsSnapshot settings,
		in LogEventContext context)
	{
		var asyncContext = context.AsyncHierarchy != null
			? string.Join(" > ", context.AsyncHierarchy)
			: null;
		var groupingContext = BuildGroupingContext(input, settings.Grouping, context, asyncContext);
		var severityEmoji = LogLevelEmoji.GetEmoji(input.Level);
		var categoryEmoji = input.Category.HasValue
			? LogCategoryEmoji.GetEmoji(input.Category.Value)
			: null;
		return new LogPayload(input.Level, input.Message,
			BuildFormattedMessage(input, settings, context, severityEmoji, categoryEmoji, groupingContext), severityEmoji)
		{
			Timestamp             = context.Timestamp,
			Category              = input.Category,
			CategoryEmoji         = categoryEmoji,
			Exception             = input.Exception,
			ExceptionType         = input.ExceptionType,
			CallerInfo            = input.CallerInfo,
			Stage                 = context.Stage,
			ContextObject         = input.ContextObject,
			StackTrace            = input.StackTrace,
			InnerExceptionType    = input.InnerExceptionType,
			InnerExceptionMessage = input.InnerExceptionMessage,
			GroupingContext       = groupingContext,
			AsyncContextHierarchy = context.AsyncHierarchy,
			AsyncContext          = asyncContext,
			AsyncContextDepth     = context.AsyncHierarchy?.Count ?? 0,
			Style                 = settings.GetStyle(input.Level)
		};
	}

	internal static LogPayload FormatStageDivider(
		string dividerLine,
		in LogInput input,
		in LogSettingsSnapshot settings,
		in LogEventContext context)
	{
		var asyncContext = context.AsyncHierarchy != null
			? string.Join(" > ", context.AsyncHierarchy)
			: null;

		return new LogPayload(LogLevel.Divider, dividerLine, dividerLine, LogLevelEmoji.GetEmoji(input.Level))
		{
			Timestamp             = context.Timestamp,
			Category              = LogCategory.None,
			CategoryEmoji         = input.Category.HasValue ? LogCategoryEmoji.GetEmoji(input.Category.Value) : null,
			Exception             = null,
			ExceptionType         = null,
			CallerInfo            = null,
			Stage                 = context.Stage,
			ContextObject         = input.ContextObject,
			StackTrace            = null,
			InnerExceptionType    = null,
			InnerExceptionMessage = null,
			GroupingContext       = null,
			AsyncContextHierarchy = context.AsyncHierarchy,
			AsyncContext          = asyncContext,
			AsyncContextDepth     = context.AsyncHierarchy?.Count ?? 0,
			Style                 = settings.GetStyle(input.Level)
		};
	}

	private static string BuildFormattedMessage(
		in LogInput input,
		in LogSettingsSnapshot settings,
		in LogEventContext context,
		string severityEmoji,
		string? categoryEmoji,
		string? groupingContext)
	{
		var lines = new List<string>();
		if (context.AsyncHierarchy != null)
			lines.Add($"🔄 [{string.Join(" > ", context.AsyncHierarchy)}]");

		lines.Add(BuildMainLine(input, settings, context, severityEmoji, categoryEmoji, groupingContext));

		if (ShouldIncludeDetails(input.Level, input.CallerInfo))
		{
			var fileLocation = GetFileLocation(input.FilePath, settings.FileLocation);
			var details = new List<string>();
			if (settings.FileLocation.Enabled && fileLocation != null)
				details.Add(fileLocation);
			if (input.CallerInfo != null)
				details.Add(input.CallerInfo);
			if (details.Count != 0)
				lines.Add($"  └─ {string.Join(" :: ", details)}");
		}

		return string.Join("\n", lines);
	}

	private static string BuildMainLine(
		in LogInput input,
		in LogSettingsSnapshot settings,
		in LogEventContext context,
		string severityEmoji,
		string? categoryEmoji,
		string? groupingContext)
	{
		var metadata = BuildMetadataSection(settings, context, groupingContext);
		var mainLine = string.IsNullOrEmpty(metadata) ? string.Empty : $"{metadata} ";
		mainLine += severityEmoji;

		if (categoryEmoji != null)
			mainLine += $" [{categoryEmoji}]";
		if (input.ExceptionType != null)
			mainLine += $" {input.ExceptionType} ::";

		return $"{mainLine} {input.Message}";
	}

	private static string BuildMetadataSection(
		in LogSettingsSnapshot settings,
		in LogEventContext context,
		string? groupingContext)
	{
		var parts = new List<string>();
		if (settings.SequenceNumber.Enabled)
			parts.Add($"#{context.SequenceNumber}");
		if (settings.Timestamp.Enabled)
			parts.Add(context.Timestamp.ToString(settings.Timestamp.Format));
		if (settings.FrameCount.Enabled && context.FrameCount.HasValue)
			parts.Add($"F{context.FrameCount.Value}");
		if (settings.ThreadId.Enabled)
			parts.Add($"T{context.ThreadId}");
		if (settings.Grouping.IncludeInOutput && groupingContext != null)
			parts.Add($"G:{groupingContext}");

		return parts.Count == 0 ? string.Empty : $"[{string.Join(" ", parts)}]";
	}

	private static string? BuildGroupingContext(
		in LogInput input,
		GroupingConfig grouping,
		in LogEventContext context,
		string? asyncContext) =>
		grouping.GroupBy switch
		{
			LoggerSettings.ContextGrouping.CallerType => ExtractTypeNameFromFilePath(input.FilePath),
			LoggerSettings.ContextGrouping.CallerMethod => input.CallerInfo,
			LoggerSettings.ContextGrouping.Category => input.Category?.ToString(),
			LoggerSettings.ContextGrouping.Thread => context.ThreadId.ToString(),
			LoggerSettings.ContextGrouping.Level => input.Level.ToString(),
			LoggerSettings.ContextGrouping.TimeWindow => GetTimeWindowGroup(context.Timestamp, grouping.TimeWindowMs),
			LoggerSettings.ContextGrouping.AsyncContext => asyncContext,
			_ => null
		};

	private static string? GetFileLocation(string? filePath, FileLocationConfig settings)
	{
		if (!settings.Enabled || filePath == null)
			return null;

		return settings.ShowFullPath ? filePath : Path.GetFileName(filePath);
	}

	private static bool ShouldIncludeDetails(LogLevel level, string? callerInfo) =>
		callerInfo != null || level is LogLevel.Warning or LogLevel.Error or LogLevel.Exception;

	private static string ExtractTypeNameFromFilePath(string? filePath)
	{
		if (string.IsNullOrEmpty(filePath))
			return "Unknown";

		return Path.GetFileNameWithoutExtension(filePath) ?? "Unknown";
	}

	private static string GetTimeWindowGroup(DateTime timestamp, int timeWindowMs)
	{
		var windowTicks = TimeSpan.FromMilliseconds(timeWindowMs).Ticks;
		return $"Window-{timestamp.Ticks / windowTicks}";
	}
}

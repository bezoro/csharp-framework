using Bezoro.Logging.Types;

namespace Bezoro.Logging.Internal;

internal readonly record struct LogSettingsSnapshot(
	FileLocationConfig FileLocation,
	FrameCountConfig FrameCount,
	GroupingConfig Grouping,
	SequenceNumberConfig SequenceNumber,
	ThreadIdConfig ThreadId,
	TimestampConfig Timestamp,
	LogStyle ErrorStyle,
	LogStyle ExceptionStyle,
	LogStyle InfoStyle,
	LogStyle SuccessStyle,
	LogStyle WarningStyle)
{
	internal static LogSettingsSnapshot Capture()
	{
		var settings = LoggerSettings.CaptureConfiguration();
		return new(
			settings.FileLocation,
			settings.FrameCount,
			settings.Grouping,
			settings.SequenceNumber,
			settings.ThreadId,
			settings.Timestamp,
			settings.ErrorStyle,
			settings.ExceptionStyle,
			settings.InfoStyle,
			settings.SuccessStyle,
			settings.WarningStyle);
	}

	internal LogStyle GetStyle(LogLevel level) => level switch
	{
		LogLevel.Info      => InfoStyle,
		LogLevel.Success   => SuccessStyle,
		LogLevel.Warning   => WarningStyle,
		LogLevel.Error     => ErrorStyle,
		LogLevel.Exception => ExceptionStyle,
		_                  => InfoStyle
	};
}

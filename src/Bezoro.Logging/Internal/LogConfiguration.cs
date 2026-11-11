using Bezoro.Logging.Types;

namespace Bezoro.Logging.Internal;

// Published once per configuration write; readers never observe a torn value-type setting.
internal sealed record LogConfiguration
{
	internal bool Enabled { get; init; } = true;
	internal FileLocationConfig FileLocation { get; init; } = FileLocationConfig.FilenameOnly;
	internal FrameCountConfig FrameCount { get; init; } = FrameCountConfig.Disabled;
	internal GroupingConfig Grouping { get; init; } = GroupingConfig.None;
	internal LogStyle ErrorStyle { get; init; } = new(ConsoleColor.Red);
	internal LogStyle ExceptionStyle { get; init; } = new(ConsoleColor.DarkRed, true);
	internal LogStyle InfoStyle { get; init; } = new(ConsoleColor.White);
	internal LogStyle SuccessStyle { get; init; } = new(ConsoleColor.Green);
	internal LogStyle WarningStyle { get; init; } = new(ConsoleColor.Yellow);
	internal SequenceNumberConfig SequenceNumber { get; init; } = SequenceNumberConfig.On;
	internal StageConfig Stage { get; init; } = StageConfig.Disabled;
	internal ThreadIdConfig ThreadId { get; init; } = ThreadIdConfig.On;
	internal TimestampConfig Timestamp { get; init; } = TimestampConfig.Default;
}

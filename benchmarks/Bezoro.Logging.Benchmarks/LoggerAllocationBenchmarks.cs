using BenchmarkDotNet.Attributes;
using Bezoro.Logging.Types;

namespace Bezoro.Logging.Benchmarks;

[MemoryDiagnoser]
public class LoggerAllocationBenchmarks
{
	private readonly Action<LogPayload> _sink = static _ => { };
	private LogLevel _minimumLevel;
	private bool _enabled;
	private FileLocationConfig _fileLocation;
	private FrameCountConfig _frameCount;
	private GroupingConfig _grouping;
	private LogStyle _errorStyle = null!;
	private LogStyle _exceptionStyle = null!;
	private LogStyle _infoStyle = null!;
	private LogStyle _successStyle = null!;
	private LogStyle _warningStyle = null!;
	private SequenceNumberConfig _sequenceNumber;
	private StageConfig _stage;
	private ThreadIdConfig _threadId;
	private TimestampConfig _timestamp;
	private LogCategory[] _mutedCategories = [];

	[Params(false, true)]
	public bool HasSubscriber { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		_minimumLevel = Logger.MinimumLevel;
		_enabled = LoggerSettings.Enabled;
		_fileLocation = LoggerSettings.FileLocation;
		_frameCount = LoggerSettings.FrameCount;
		_grouping = LoggerSettings.Grouping;
		_errorStyle = LoggerSettings.ErrorStyle;
		_exceptionStyle = LoggerSettings.ExceptionStyle;
		_infoStyle = LoggerSettings.InfoStyle;
		_successStyle = LoggerSettings.SuccessStyle;
		_warningStyle = LoggerSettings.WarningStyle;
		_sequenceNumber = LoggerSettings.SequenceNumber;
		_stage = LoggerSettings.Stage;
		_threadId = LoggerSettings.ThreadId;
		_timestamp = LoggerSettings.Timestamp;
		_mutedCategories = [.. LoggerSettings.MutedCategories];

		Logger.MinimumLevel = LogLevel.Info;
		LoggerSettings.Enabled = true;
		LoggerSettings.FileLocation = FileLocationConfig.Disabled;
		LoggerSettings.FrameCount = FrameCountConfig.Disabled;
		LoggerSettings.Grouping = GroupingConfig.None;
		LoggerSettings.SequenceNumber = SequenceNumberConfig.Off;
		LoggerSettings.Stage = StageConfig.Disabled;
		LoggerSettings.ThreadId = ThreadIdConfig.Off;
		LoggerSettings.Timestamp = TimestampConfig.Disabled;
		LoggerSettings.MutedCategories.Clear();
		if (HasSubscriber) Logger.OnLog += _sink;
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		Logger.OnLog -= _sink;
		Logger.MinimumLevel = _minimumLevel;
		LoggerSettings.Enabled = _enabled;
		LoggerSettings.FileLocation = _fileLocation;
		LoggerSettings.FrameCount = _frameCount;
		LoggerSettings.Grouping = _grouping;
		LoggerSettings.ErrorStyle = _errorStyle;
		LoggerSettings.ExceptionStyle = _exceptionStyle;
		LoggerSettings.InfoStyle = _infoStyle;
		LoggerSettings.SuccessStyle = _successStyle;
		LoggerSettings.WarningStyle = _warningStyle;
		LoggerSettings.SequenceNumber = _sequenceNumber;
		LoggerSettings.Stage = _stage;
		LoggerSettings.ThreadId = _threadId;
		LoggerSettings.Timestamp = _timestamp;
		LoggerSettings.MutedCategories.Clear();
		LoggerSettings.MutedCategories.UnionWith(_mutedCategories);
	}

	[Benchmark(Baseline = true)]
	public void LogWithoutAsyncContext() => Logger.Log("message");

	[Benchmark]
	public void LogWithNestedAsyncContext()
	{
		using var outer = LoggerSettings.BeginAsyncContext("outer");
		using var inner = LoggerSettings.BeginAsyncContext("inner");
		Logger.Log("message");
	}
}

using Bezoro.Logging.Types;

namespace Bezoro.Logging.Tests;

public sealed class LoggerWorkContractTests
{
	[Fact]
	public void InvalidLevel_RejectsBeforeInvokingMetadata()
	{
		using var settings = new LoggerSettingsScope();
		LoggerSettings.Stage = StageConfig.Create(() => throw new InvalidOperationException("Invalid input must not run providers."));
		Action<LogPayload> sink = _ => { };
		Logger.OnLog += sink;
		try { Assert.Throws<ArgumentOutOfRangeException>(() => Logger.Log("invalid", (LogLevel)99)); }
		finally { Logger.OnLog -= sink; }
	}

	[Fact]
	public void TimerThatStartsFiltered_DoesNotEmitAfterLoggingIsEnabled()
	{
		using var settings = new LoggerSettingsScope();
		var messages = new List<LogPayload>();
		Action<LogPayload> sink = messages.Add;
		Logger.OnLog += sink;
		try
		{
			LoggerSettings.Enabled = false;
			var timer = Logger.BeginTimer("filtered operation");
			LoggerSettings.Enabled = true;
			timer.Dispose();
			Assert.Empty(messages);
		}
		finally { Logger.OnLog -= sink; }
	}

	[Fact]
	public void DefaultTimer_DoesNotReportAnOperation()
	{
		using var settings = new LoggerSettingsScope();
		var messages = new List<LogPayload>();
		Action<LogPayload> sink = messages.Add;
		Logger.OnLog += sink;
		try { default(Utilities.PerformanceTimer).Dispose(); }
		finally { Logger.OnLog -= sink; }
		Assert.Empty(messages);
	}

	[Fact]
	public void NoSubscribers_SkipFormattingAndMetadataProviders()
	{
		using var settings = new LoggerSettingsScope();
		LoggerSettings.Stage = StageConfig.Create(() => throw new InvalidOperationException("Stage provider should be skipped."));
		LoggerSettings.FrameCount = FrameCountConfig.Create(() => throw new InvalidOperationException("Frame provider should be skipped."));
		Assert.False(Logger.IsEnabled());
		Logger.Log(new UnformattableMessage());
	}

	[Theory]
	[InlineData(LogLevel.Warning)]
	[InlineData(LogLevel.Error)]
	[InlineData(LogLevel.Exception)]
	public void Success_HasLowerSeverityThanWarnings(LogLevel minimum)
	{
		using var settings = new LoggerSettingsScope();
		Logger.MinimumLevel = minimum;
		var messages = new List<LogPayload>();
		Action<LogPayload> sink = messages.Add;
		Logger.OnLog += sink;
		try
		{
			Assert.False(Logger.IsEnabled(LogLevel.Success));
			Logger.LogSuccess(new UnformattableMessage());
			Assert.Empty(messages);
		}
		finally { Logger.OnLog -= sink; }
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void TimeWindow_RejectsNonPositiveDuration(int milliseconds) =>
		Assert.Throws<ArgumentOutOfRangeException>(() => GroupingConfig.Create(LoggerSettings.ContextGrouping.TimeWindow, timeWindowMs: milliseconds));

	[Fact]
	public void NullMessage_IsRepresentedExplicitly()
	{
		using var settings = new LoggerSettingsScope();
		LogPayload? received = null;
		Action<LogPayload> sink = payload => received = payload;
		Logger.OnLog += sink;
		try { Logger.Log((object?)null); }
		finally { Logger.OnLog -= sink; }
		Assert.Equal("null", received!.Message);
	}

	private sealed class UnformattableMessage
	{
		public override string ToString() => throw new InvalidOperationException("Message should be skipped.");
	}
}

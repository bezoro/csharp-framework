using Bezoro.Logging.Types;
using FluentAssertions;

namespace Bezoro.Logging.Tests;

public sealed class LoggerStageTests
{
	[Fact]
	public void Log_WhenStageChangesAndRepeats_ShouldEmitDividerOnlyBeforeChangedStagePayload()
	{
		using var settings = new LoggerSettingsScope();
		var firstStage = $"first-{Guid.NewGuid():N}";
		var secondStage = $"second-{Guid.NewGuid():N}";
		var currentStage = firstStage;
		var frameProviderCalls = 0;
		var stageProviderCalls = 0;
		LoggerSettings.Stage = StageConfig.Create(() =>
		{
			stageProviderCalls++;
			return currentStage;
		});
		LoggerSettings.FrameCount = FrameCountConfig.Create(() =>
		{
			frameProviderCalls++;
			return 42;
		});
		LoggerSettings.SequenceNumber = SequenceNumberConfig.On;
		Action<LogPayload> primeSink = _ => { };
		Logger.OnLog += primeSink;
		try { Logger.Log("prime"); }
		finally { Logger.OnLog -= primeSink; }
		var payloads = new List<LogPayload>();
		Action<LogPayload> handler = payloads.Add;
		Logger.OnLog += handler;

		try
		{
			currentStage = secondStage;
			Logger.Log("second");
			Logger.Log("repeated");
		}
		finally
		{
			Logger.OnLog -= handler;
		}

		payloads.Should().HaveCount(3);
		payloads[0].Level.Should().Be(LogLevel.Divider);
		payloads[0].Message.Should().Be($"[{firstStage}] ==============> [{secondStage}]");
		payloads[0].Stage.Should().Be(secondStage);

		payloads[1].Level.Should().Be(LogLevel.Info);
		payloads[1].Message.Should().Be("second");
		payloads[1].Stage.Should().Be(secondStage);

		payloads[2].Level.Should().Be(LogLevel.Info);
		payloads[2].Message.Should().Be("repeated");
		payloads[2].Stage.Should().Be(secondStage);

		var secondSequence = ParseSequenceNumber(payloads[1]);
		ParseSequenceNumber(payloads[2]).Should().Be(secondSequence + 1);
		stageProviderCalls.Should().Be(3);
		frameProviderCalls.Should().Be(3);
	}

	[Fact]
	public void Log_WhenStageProviderReplacesSettings_ShouldUseFreshDividerProvider()
	{
		using var settings = new LoggerSettingsScope();
		var firstStage = $"first-{Guid.NewGuid():N}";
		var secondStage = $"second-{Guid.NewGuid():N}";
		PrimeStage(firstStage);
		LoggerSettings.Stage = StageConfig.Create(
			() =>
			{
				LoggerSettings.Stage = StageConfig.Create(() => secondStage, (_, _) => "fresh divider");
				return secondStage;
			},
			(_, _) => "stale divider");
		var payloads = new List<LogPayload>();
		Action<LogPayload> handler = payloads.Add;
		Logger.OnLog += handler;

		try
		{
			Logger.Log("message");
		}
		finally
		{
			Logger.OnLog -= handler;
		}

		payloads.Should().HaveCount(2);
		payloads[0].Level.Should().Be(LogLevel.Divider);
		payloads[0].Message.Should().Be("fresh divider");
	}

	[Fact]
	public void Log_WhenDividerProviderChangesStyle_ShouldUseChangedStyleForDivider()
	{
		using var settings = new LoggerSettingsScope();
		var firstStage = $"first-{Guid.NewGuid():N}";
		var secondStage = $"second-{Guid.NewGuid():N}";
		var initialStyle = new LogStyle(ConsoleColor.Yellow);
		var dividerStyle = new LogStyle(ConsoleColor.DarkYellow, true);
		LoggerSettings.WarningStyle = initialStyle;
		PrimeStage(firstStage);
		LoggerSettings.Stage = StageConfig.Create(
			() => secondStage,
			(_, _) =>
			{
				LoggerSettings.WarningStyle = dividerStyle;
				return "divider";
			});
		var payloads = new List<LogPayload>();
		Action<LogPayload> handler = payloads.Add;
		Logger.OnLog += handler;

		try
		{
			Logger.Log("message", LogLevel.Warning);
		}
		finally
		{
			Logger.OnLog -= handler;
		}

		payloads.Should().HaveCount(2);
		payloads[0].Style.Should().BeSameAs(dividerStyle);
	}

	[Fact]
	public void Log_WhenDividerSubscriberChangesStyle_ShouldUseChangedStyleForOrdinaryPayload()
	{
		using var settings = new LoggerSettingsScope();
		var firstStage = $"first-{Guid.NewGuid():N}";
		var secondStage = $"second-{Guid.NewGuid():N}";
		var dividerStyle = new LogStyle(ConsoleColor.Yellow);
		var ordinaryStyle = new LogStyle(ConsoleColor.DarkYellow, true);
		LoggerSettings.WarningStyle = dividerStyle;
		PrimeStage(firstStage);
		LoggerSettings.Stage = StageConfig.Create(() => secondStage, (_, _) => "divider");
		var payloads = new List<LogPayload>();
		Action<LogPayload> handler = payload =>
		{
			payloads.Add(payload);
			if (payload.Level == LogLevel.Divider)
				LoggerSettings.WarningStyle = ordinaryStyle;
		};
		Logger.OnLog += handler;

		try
		{
			Logger.Log("message", LogLevel.Warning);
		}
		finally
		{
			Logger.OnLog -= handler;
		}

		payloads.Should().HaveCount(2);
		payloads[0].Style.Should().BeSameAs(dividerStyle);
		payloads[1].Style.Should().BeSameAs(ordinaryStyle);
	}

	[Fact]
	public void Log_WhenStageChanges_ShouldInvokeCallbacksInBaselineOrder()
	{
		using var settings = new LoggerSettingsScope();
		var firstStage = $"first-{Guid.NewGuid():N}";
		var secondStage = $"second-{Guid.NewGuid():N}";
		PrimeStage(firstStage);
		var callbacks = new List<string>();
		LoggerSettings.Stage = StageConfig.Create(
			() =>
			{
				callbacks.Add("stage provider");
				return secondStage;
			},
			(_, _) =>
			{
				callbacks.Add("divider provider");
				return "divider";
			});
		LoggerSettings.FrameCount = FrameCountConfig.Create(() =>
		{
			callbacks.Add("frame provider");
			return 42;
		});
		Action<LogPayload> handler = payload =>
			callbacks.Add(payload.Level == LogLevel.Divider ? "divider subscriber" : "ordinary subscriber");
		Logger.OnLog += handler;

		try
		{
			Logger.Log("message");
		}
		finally
		{
			Logger.OnLog -= handler;
		}

		callbacks.Should().Equal(
			"stage provider",
			"divider provider",
			"divider subscriber",
			"frame provider",
			"ordinary subscriber");
	}

	private static void PrimeStage(string stage)
	{
		LoggerSettings.Stage = StageConfig.Create(() => stage);
		Action<LogPayload> primeSink = _ => { };
		Logger.OnLog += primeSink;
		try { Logger.Log("prime"); }
		finally { Logger.OnLog -= primeSink; }
	}

	private static long ParseSequenceNumber(LogPayload payload)
	{
		var endIndex = payload.FormattedMessage.IndexOf(' ');
		return long.Parse(payload.FormattedMessage[2..endIndex]);
	}
}

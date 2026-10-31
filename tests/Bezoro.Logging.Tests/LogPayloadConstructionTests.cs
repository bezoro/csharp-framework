using Bezoro.Logging.Types;

namespace Bezoro.Logging.Tests;

public sealed class LogPayloadConstructionTests
{
	[Fact]
	public void Constructor_CapturesRequiredValues()
	{
		var payload = new LogPayload(LogLevel.Info, "message", "formatted", "marker");
		Assert.Equal(LogLevel.Info, payload.Level);
		Assert.Equal("message", payload.Message);
		Assert.Equal("formatted", payload.FormattedMessage);
		Assert.Equal("marker", payload.SeverityEmoji);
	}

	[Fact]
	public void Constructor_RejectsUndefinedLevelAndNullDisplayValues()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new LogPayload((LogLevel)99, "message", "formatted", "marker"));
		Assert.Throws<ArgumentNullException>(() => new LogPayload(LogLevel.Info, null!, "formatted", "marker"));
		Assert.Throws<ArgumentNullException>(() => new LogPayload(LogLevel.Info, "message", null!, "marker"));
		Assert.Throws<ArgumentNullException>(() => new LogPayload(LogLevel.Info, "message", "formatted", null!));
	}
}

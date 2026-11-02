using Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;
using FluentAssertions;

namespace Bezoro.Chess.UCI.Protocol.Tests.API.Types;

public class UciWdlTests
{
	[Fact]
	public void Formatting_DoesNotRecursivelyFormatOpponentPerspectiveOrEvaluateInvalidDefaults()
	{
		new UciWdl(300, 500, 200).ToString().Should().Be("300 500 200");
		default(UciWdl).ToString().Should().Be("0 0 0");
	}

	[Theory]
	[InlineData(1000, 0, 0, 1)]
	[InlineData(0, 1000, 0, 0.5)]
	[InlineData(0, 0, 1000, 0)]
	[InlineData(300, 500, 200, 0.55)]
	public void Distribution_ShouldExposeExpectedPointsAndOpponentPerspective(int wins, int draws, int losses, double expected)
	{
		var wdl = new UciWdl(wins, draws, losses);
		wdl.IsValid.Should().BeTrue();
		wdl.ExpectedPoints.Should().BeApproximately(expected, 1e-12);
		wdl.OpponentPerspective.ExpectedPoints.Should().BeApproximately(1 - expected, 1e-12);
		wdl.OpponentPerspective.OpponentPerspective.Should().Be(wdl);
	}

	[Theory]
	[InlineData(-1, 501, 500)]
	[InlineData(0, -1, 1001)]
	[InlineData(0, 0, -1)]
	[InlineData(1001, 0, 0)]
	[InlineData(0, 1001, 0)]
	[InlineData(0, 0, 1001)]
	[InlineData(100, 100, 100)]
	[InlineData(1000, 1000, 1000)]
	public void Distribution_ShouldRejectInvalidValues(int wins, int draws, int losses)
	{
		FluentActions.Invoking(() => new UciWdl(wins, draws, losses)).Should().Throw<ArgumentException>();
	}

	[Fact]
	public void DefaultDistribution_ShouldNotPretendToBeAnEngineEstimate()
	{
		UciWdl wdl = default;
		wdl.IsValid.Should().BeFalse();
		FluentActions.Invoking(() => wdl.ExpectedPoints).Should().Throw<InvalidOperationException>();
	}

	[Fact]
	public void Parser_ShouldPropagateWdlAndScoreBoundToTypedPayloadPvAndFinalResult()
	{
		const string line = "info depth 12 score cp 25 lowerbound wdl 300 500 200 nodes 100 time 5 pv e2e4 e7e5";
		UciProtocolParser.TryParse(line, out var message).Should().BeTrue();
		message.Info!.Value.Payload.Wdl.Should().Be(new UciWdl(300, 500, 200));
		message.Info.Value.Payload.PrincipalVariation!.Value.Wdl.Should().Be(new UciWdl(300, 500, 200));
		message.Info.Value.Payload.PrincipalVariation.Value.ScoreBound.Should().Be(UciScoreBound.Lower);
		PrincipalVariation.TryParse(line, out var pv).Should().BeTrue();
		pv.Wdl.Should().Be(new UciWdl(300, 500, 200));
		SearchResult.TryParse([line, "bestmove e2e4"], out var result).Should().BeTrue();
		result.BestPv!.Value.Wdl.Should().Be(new UciWdl(300, 500, 200));
	}

	[Theory]
	[InlineData("-1 501 500")]
	[InlineData("1001 0 0")]
	[InlineData("100 100 100")]
	[InlineData("300 500")]
	[InlineData("not-a-number 500 200")]
	public void Parser_ShouldIgnoreMalformedDistributionAndKeepFollowingFields(string distribution)
	{
		UciProtocolParser.TryParse($"info wdl {distribution} depth 5 score cp 20 pv e2e4", out var message).Should().BeTrue();
		message.Info!.Value.Payload.Wdl.Should().BeNull();
		message.Info.Value.Payload.Depth.Should().Be(5);
		message.Info.Value.Payload.PrincipalVariation!.Value.Wdl.Should().BeNull();
	}

	[Fact]
	public void Parser_ShouldParseWdlWithoutPvAndKeepRefutationSeparated()
	{
		UciProtocolParser.TryParse("info refutation e2e4 e7e5 wdl 300 500 200 depth 7", out var message).Should().BeTrue();
		message.Info!.Value.Payload.Wdl.Should().Be(new UciWdl(300, 500, 200));
		message.Info.Value.Payload.Refutation.Should().Equal("e2e4", "e7e5");
		message.Info.Value.Payload.PrincipalVariation.Should().BeNull();
	}
}

using FluentAssertions;
using JetBrains.Annotations;

namespace Bezoro.Chess.UCI.Protocol.Tests.API.Types;

[TestSubject(typeof(PositionAdvantage))]
public class PositionAdvantageTests
{
	[Fact]
	public void FromScore_HandlesMinimumCentipawnAndMateValues()
	{
		var centipawns = PositionAdvantage.FromScore(new(int.MinValue, null));
		centipawns.Normalized.Should().BeNegative();
		centipawns.Summary.Should().Contain("2147483648 cp");
		var mate = PositionAdvantage.FromScore(new(null, int.MinValue));
		mate.Normalized.Should().BeNegative();
		mate.Summary.Should().Contain("mate in 2147483648");
	}
	[Fact]
	public void FromScore_WhenCentipawnScoreIsPositive_ShouldProducePlayerRelativeSummary()
	{
		var advantage = PositionAdvantage.FromScore(new(48, null));

		advantage.Score.Cp.Should().Be(48);
		advantage.Summary.Should().Contain("48 cp");
		advantage.Normalized.Should().BePositive();
	}

	[Fact]
	public void FromScore_WhenMateScoreFavoursPlayer_ShouldDescribeWinningMate()
	{
		var advantage = PositionAdvantage.FromScore(new(null, 2));

		advantage.Score.Mate.Should().Be(2);
		advantage.Summary.Should().Contain("You mate in 2");
		advantage.Normalized.Should().BePositive();
	}

	[Fact]
	public void FromEngineScore_WhenScoreIsEven_ShouldProduceNeutralSummary()
	{
		var advantage = PositionAdvantage.FromEngineScore(
			rawCpScore: 0,
			rawMateScore: null,
			sideToMove: 'w',
			playerColor: 'w'
		);

		advantage.Normalized.Should().Be(0);
		advantage.Summary.Should().Be("Advantage 0.00 | Even (0 cp)");
		advantage.Score.Cp.Should().Be(0);
	}

	[Fact]
	public void FromEngineScore_WhenMateScoreFavoursPlayer_ShouldDescribeWinningMate()
	{
		var advantage = PositionAdvantage.FromEngineScore(
			rawCpScore: null,
			rawMateScore: 2,
			sideToMove: 'w',
			playerColor: 'w'
		);

		advantage.Normalized.Should().BePositive();
		advantage.Summary.Should().Contain("You mate in 2");
		advantage.Score.Mate.Should().Be(2);
	}

	[Fact]
	public void FromEngineScore_WhenBlackToMoveIsLosingForWhitePlayer_ShouldReturnPositiveWhiteAdvantage()
	{
		var advantage = PositionAdvantage.FromEngineScore(
			rawCpScore: -95,
			rawMateScore: null,
			sideToMove: 'b',
			playerColor: 'w'
		);

		advantage.Score.Cp.Should().Be(95);
		advantage.Summary.Should().Contain("95 cp");
	}
}

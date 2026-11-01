using Bezoro.Chess.UCI.API.Types;
using Bezoro.Chess.UCI.Tests.TestHelpers;
using FluentAssertions;
using JetBrains.Annotations;
using Xunit.Abstractions;

namespace Bezoro.Chess.UCI.Tests.API.Types;

[TestSubject(typeof(MoveScore))]
public class MoveScoreTests(ITestOutputHelper output) : UnitTestBase(output)
{
	[Theory]
	[InlineData("info string engine reports score cp 99")]
	[InlineData("info depth 8 pv e2e4 score cp 99")]
	[InlineData("unrelated score cp 99")]
	public void TryParse_DoesNotReadScoresInsideTextOrPrincipalVariations(string line)
	{
		MoveScore.TryParse(line, out var score).Should().BeFalse();
		score.Should().BeNull();
	}

	[Fact]
	public void TryParse_UsesTheLastScoreAndAcceptsRepeatedSpaces()
	{
		MoveScore.TryParse(" info  depth 8 score mate -2 score cp 34 pv e2e4 ", out var score).Should().BeTrue();
		score!.Value.ScoreCp.Should().Be(34);
		score.Value.ScoreMate.Should().BeNull();
	}

	[Fact]
	public void FromSearchResult_DefaultResultHasNoScore()
	{
		var score = MoveScore.FromSearchResult(default);
		score.ScoreCp.Should().BeNull();
		score.ScoreMate.Should().BeNull();
	}
	[Fact]
	public void FromSearchResult_WhenMateScoreAvailable_ShouldUseMate()
	{
		Log("Testing FromSearchResult with mate score");
		var pv = new PrincipalVariation(
			10,
			10,
			1,
			null,
			-2,
			1000,
			100_000,
			0,
			50,
			["f7g7"],
			"f7g7"
		);

		var result = new SearchResult(
			10,
			10,
			1,
			1000,
			0,
			50,
			[pv],
			"f7g7",
			string.Empty
		);

		var score = MoveScore.FromSearchResult(result);

		score.ScoreMate.Should().Be(-2);
		score.ScoreCp.Should().BeNull();
	}

	[Fact]
	public void FromSearchResult_WhenPrimaryPvMissingCp_ShouldFallBackToFirstAvailable()
	{
		Log("Testing FromSearchResult fallback to first available");
		var pv1 = new PrincipalVariation(
			8,
			8,
			1,
			null,
			null,
			500,
			50_000,
			0,
			20,
			["e2e4"],
			"e2e4"
		);

		var pv2 = new PrincipalVariation(
			8,
			9,
			2,
			25,
			null,
			600,
			60_000,
			0,
			25,
			["d2d4"],
			"d2d4"
		);

		var result = new SearchResult(
			8,
			9,
			2,
			1100,
			0,
			45,
			[pv1, pv2],
			"e2e4",
			string.Empty
		);

		var score = MoveScore.FromSearchResult(result);

		score.ScoreCp.Should().Be(25);
		score.ScoreMate.Should().BeNull();
	}

	[Fact]
	public void TryParse_WhenInvalidInput_ShouldReturnFalseAndNull()
	{
		Log("Testing TryParse with invalid input");
		var  line    = "invalid";
		bool success = MoveScore.TryParse(line, out var result);

		success.Should().BeFalse();
		result.Should().BeNull();
	}

	[Fact]
	public void TryParse_WhenValidInput_ShouldReturnTrueAndValidObject()
	{
		Log("Testing TryParse with valid input");
		var line =
			"info depth 12 seldepth 20 multipv 1 score cp 34 nodes 1000 nps 5000 tbhits 2 time 50 pv e2e4 e7e5 g1f3";

		bool success = MoveScore.TryParse(line, out var moveScore);

		success.Should().BeTrue();
		moveScore.Should().NotBeNull();
		moveScore!.Value.ScoreCp.Should().Be(34);
	}
}

using FluentAssertions;
using JetBrains.Annotations;

namespace Bezoro.Chess.UCI.Protocol.Tests.API.Types;

[TestSubject(typeof(PrincipalVariation))]
public class PrincipalVariationTests
{
	[Fact]
	public void TryParse_WhenWithCpScore_ShouldParseFieldsAndMoves()
	{
		const string LINE =
			"info depth 12 seldepth 20 multipv 1 score cp 34 nodes 123456 nps 2500000 tbhits 0 time 123 pv e2e4 e7e5 g1f3";

		bool ok = PrincipalVariation.TryParse(LINE, out var pv);

		ok.Should().BeTrue();
		pv.Depth.Should().Be(12u);
		pv.SelDepth.Should().Be(20u);
		pv.MultiPv.Should().Be(1u);
		pv.ScoreCp.Should().Be(34);
		pv.ScoreMate.Should().BeNull();
		pv.Nodes.Should().Be(123456u);
		pv.Nps.Should().Be(2500000u);
		pv.TbHits.Should().Be(0u);
		pv.Time.Should().Be(123u);
		pv.Moves.Should().ContainInOrder("e2e4", "e7e5", "g1f3");
		pv.RawPv.Should().Be("e2e4 e7e5 g1f3");
	}

	[Fact]
	public void TryParse_WhenWithMateScore_ShouldParseMateAndNoCp()
	{
		const string LINE =
			"info depth 8 seldepth 12 multipv 2 score mate -3 nodes 50000 tbhits 0 time 1500 pv e2e4 e7e5";

		bool ok = PrincipalVariation.TryParse(LINE, out var pv);

		ok.Should().BeTrue();
		pv.ScoreMate.Should().Be(-3);
		pv.ScoreCp.Should().BeNull();
		pv.Moves.Should().ContainInOrder("e2e4", "e7e5");
	}

	[Fact]
	public void TryParse_WhenWithoutPv_ShouldReturnFalse()
	{
		const string LINE = "info depth 10 seldepth 10 multipv 1 score cp 10 nodes 1";

		bool ok = PrincipalVariation.TryParse(LINE, out var pv);

		ok.Should().BeFalse();
		pv.Moves.Should().BeNullOrEmpty();
	}

	[Fact]
	public void TryParse_WhenLineUsesSupportedCasingAndSpacing_ShouldPreserveMoveCasing()
	{
		bool ok = PrincipalVariation.TryParse(
			"INFO   depth 12 score cp 34   pv E2E4 e7e5",
			out var pv
		);

		ok.Should().BeTrue();
		pv.Depth.Should().Be(12u);
		pv.Moves.Should().Equal("E2E4", "e7e5");
	}

	[Fact]
	public void TryParse_WhenPvKeywordUsesUppercase_ShouldReturnFalse()
	{
		bool ok = PrincipalVariation.TryParse("info depth 12 PV e2e4", out _);

		ok.Should().BeFalse();
	}

	[Fact]
	public void TryParse_WhenTokensUseTabs_ShouldParseVariation()
	{
		bool ok = PrincipalVariation.TryParse("info\tdepth\t12\tpv\te2e4", out var value);

		ok.Should().BeTrue();
		value.Moves.Should().Equal("e2e4");
	}

	[Fact]
	public void TryParse_WhenFieldsFollowPv_ShouldKeepThemInTheTerminalMoveSequence()
	{
		bool ok = PrincipalVariation.TryParse("info depth 12 pv e2e4 depth 99", out var pv);

		ok.Should().BeTrue();
		pv.Depth.Should().Be(12u);
		pv.Moves.Should().Equal("e2e4", "depth", "99");
	}

	[Fact]
	public void TryParse_WhenLineHasOuterWhitespace_ShouldParseTrimmedInfoPayload()
	{
		bool ok = PrincipalVariation.TryParse(
			"  info depth 12 score cp 34 pv e2e4 e7e5  ",
			out var pv
		);

		ok.Should().BeTrue();
		pv.Depth.Should().Be(12u);
		pv.Moves.Should().Equal("e2e4", "e7e5");
	}

	[Theory]
	[InlineData("info score cp 40 score MATE -2 pv e2e4", null, -2)]
	[InlineData("info score mate 2 score CP 40 pv e2e4", 40, null)]
	[InlineData("info score cp 40 score unknown 12 pv e2e4", null, null)]
	[InlineData("info score cp 40 score cp invalid pv e2e4", null, null)]
	public void TryParse_WhenScoreClausesRepeat_ShouldUseLastCompleteClause(
		string line,
		int? expectedCentipawns,
		int? expectedMate)
	{
		bool ok = PrincipalVariation.TryParse(line, out var pv);

		ok.Should().BeTrue();
		pv.ScoreCp.Should().Be(expectedCentipawns);
		pv.ScoreMate.Should().Be(expectedMate);
	}

	[Fact]
	public void TryParse_WhenStringContainsPvToken_ShouldTreatStringAsTerminal()
	{
		bool ok = PrincipalVariation.TryParse("info depth 12 string pv e2e4", out var pv);

		ok.Should().BeFalse();
		pv.Moves.IsDefault.Should().BeFalse();
		pv.Moves.Should().BeEmpty();
	}

	[Fact]
	public void TryParse_WhenPvHasNoMoves_ShouldReturnInitializedEmptyMoves()
	{
		bool ok = PrincipalVariation.TryParse("info depth 12 pv", out var pv);

		ok.Should().BeFalse();
		pv.Moves.IsDefault.Should().BeFalse();
		pv.Moves.Should().BeEmpty();
	}
}

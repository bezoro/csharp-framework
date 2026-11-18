using Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;
using FluentAssertions;
using JetBrains.Annotations;

namespace Bezoro.Chess.UCI.Protocol.Tests.Domain.Common.Helpers;

[TestSubject(typeof(UciProtocolParser))]
public class UciProtocolParserTests
{
	[Fact]
	public void MixedWhitespaceBestMoveCompletesTranscriptAndRejectsMalformedFinalReply()
	{
		Assert.True(SearchResult.TryParse([" \tbestmove\te2e4 \tPONDER\te7e5  "], out var result));
		Assert.Equal("e2e4", result.BestMove);
		Assert.Equal("e7e5", result.PonderMove);
		Assert.False(SearchResult.TryParse(["bestmove e2e4", "bestmove\tinvalid"], out _));
		Assert.False(UciProtocolParser.TryParse("bestmoves\te2e4", out _));
	}

	[Fact]
	public void MixedWhitespacePreservesInfoFieldsAndNormalizesRetainedText()
	{
		const string line = "\tinfo\tdepth nope depth\t17 score\tcp -21 upperbound wdl 50\t900 50 " +
			"refutation\td2d4  d7d5 currline\t2 e2e4\te7e5 pv  e2e4\t e7e5  g1f3 ";
		Assert.True(UciProtocolParser.TryParse(line, out var message));
		var info = message.Info!.Value.Payload;
		Assert.Equal((uint)17, info.Depth);
		Assert.Equal(new UciWdl(50, 900, 50), info.Wdl);
		Assert.Equal(new[] { "d2d4", "d7d5" }, info.Refutation);
		Assert.Equal(new[] { "e2e4", "e7e5" }, info.CurrentLine);
		Assert.Equal((uint)2, info.CurrentLineCpu);
		Assert.True(PrincipalVariation.TryParse(line, out var pv));
		Assert.Equal(-21, pv.ScoreCp);
		Assert.Equal(UciScoreBound.Upper, pv.ScoreBound);
		Assert.Equal(info.Wdl, pv.Wdl);
		Assert.Equal("e2e4 e7e5 g1f3", pv.RawPv);
		Assert.Equal(pv.RawPv, info.PrincipalVariation!.Value.RawPv);
		Assert.Equal(new[] { "e2e4", "e7e5", "g1f3" }, pv.Moves);
		Assert.True(UciProtocolParser.TryParse("info\tstring  hello\t  world", out message));
		Assert.Equal("hello world", message.Info!.Value.Payload.String);
	}

	[Fact]
	public void TryParse_WhenBestMoveLineIsProvided_ShouldReturnTypedMessage()
	{
		const string LINE = "bestmove e2e4 ponder e7e5";

		bool parsed = UciProtocolParser.TryParse(LINE, out var message);

		parsed.Should().BeTrue();
		message.Type.Should().Be(UciProtocolMessageType.BestMove);
		message.BestMove.HasValue.Should().BeTrue();
		var bestMove = message.BestMove!.Value;
		bestMove.BestMove.Should().Be("e2e4");
		bestMove.PonderMove.Should().Be("e7e5");
		bestMove.RawLine.Should().Be(LINE);
	}

	[Fact]
	public void TryParse_WhenCopyProtectionLineIsProvided_ShouldReturnTypedMessage()
	{
		bool parsed = UciProtocolParser.TryParse("copyprotection checking", out var message);

		parsed.Should().BeTrue();
		message.CopyProtection.HasValue.Should().BeTrue();
		message.CopyProtection!.Value.State.Should().Be(UciProtectionState.Checking);
	}

	[Fact]
	public void TryParse_WhenIdLineIsProvided_ShouldReturnTypedMessage()
	{
		bool parsed = UciProtocolParser.TryParse("id name Stockfish 17", out var message);

		parsed.Should().BeTrue();
		message.Id.HasValue.Should().BeTrue();
		var idMessage = message.Id!.Value;
		idMessage.Kind.Should().Be(UciIdKind.Name);
		idMessage.Value.Should().Be("Stockfish 17");
	}

	[Fact]
	public void TryParse_WhenInfoLineContainsRichFields_ShouldPopulateTypedPayload()
	{
		const string LINE =
			"info depth 20 seldepth 32 multipv 2 score cp 34 lowerbound nodes 12345 nps 456789 hashfull 12 cpuload 876 time 250 tbhits 4 currmove e2e4 currmovenumber 7 refutation d2d4 d7d5 currline 1 e2e4 e7e5 g1f3 pv e2e4 e7e5 g1f3";

		bool parsed = UciProtocolParser.TryParse(LINE, out var message);
		bool principalVariationParsed = PrincipalVariation.TryParse(LINE, out var standalonePrincipalVariation);

		parsed.Should().BeTrue();
		principalVariationParsed.Should().BeTrue();
		message.Info.HasValue.Should().BeTrue();
		var info = message.Info!.Value.Payload;
		info.Depth.Should().Be(20u);
		info.SelDepth.Should().Be(32u);
		info.MultiPv.Should().Be(2u);
		info.Score.Should().NotBeNull();
		info.Score!.Value.Centipawns.Should().Be(34);
		info.Score.Value.Bound.Should().Be(UciScoreBound.Lower);
		info.Nodes.Should().Be(12345u);
		info.Nps.Should().Be(456789u);
		info.HashFull.Should().Be(12u);
		info.CpuLoad.Should().Be(876u);
		info.Time.Should().Be(250u);
		info.TbHits.Should().Be(4u);
		info.CurrentMove.Should().Be("e2e4");
		info.CurrentMoveNumber.Should().Be(7u);
		info.Refutation.Should().Equal("d2d4", "d7d5");
		info.CurrentLineCpu.Should().Be(1u);
		info.CurrentLine.Should().Equal("e2e4", "e7e5", "g1f3");
		info.PrincipalVariation.Should().NotBeNull();
		var principalVariation = info.PrincipalVariation!.Value;
		principalVariation.RawPv.Should().Be("e2e4 e7e5 g1f3");
		principalVariation.Should().BeEquivalentTo(
			standalonePrincipalVariation,
			options => options
				.ComparingByMembers<PrincipalVariation>()
				.Excluding(variation => variation.Moves)
		);
		principalVariation.Moves.Should().Equal(standalonePrincipalVariation.Moves);
	}

	[Fact]
	public void TryParse_WhenInfoStringLineIsProvided_ShouldCaptureEngineMessage()
	{
		bool parsed = UciProtocolParser.TryParse("info string NNUE evaluation enabled", out var message);

		parsed.Should().BeTrue();
		message.Info.HasValue.Should().BeTrue();
		message.Info!.Value.Payload.String.Should().Be("NNUE evaluation enabled");
	}

	[Fact]
	public void TryParse_WhenInfoLineUsesSupportedCasingAndSpacing_ShouldPreserveMoveCasing()
	{
		bool parsed = UciProtocolParser.TryParse(
			"INFO   depth 12 score CP 34 LOWERBOUND   pv E2E4 e7e5",
			out var message
		);

		parsed.Should().BeTrue();
		var info = message.Info!.Value.Payload;
		info.Depth.Should().Be(12u);
		info.Score.Should().Be(new UciInfoScore(34, null, UciScoreBound.Lower));
		info.PrincipalVariation!.Value.Moves.Should().Equal("E2E4", "e7e5");
	}

	[Fact]
	public void TryParse_WhenInfoFieldOrPvKeywordUsesUppercase_ShouldNotTreatItAsCanonicalGrammar()
	{
		bool parsed = UciProtocolParser.TryParse("info DEPTH 12 PV e2e4", out var message);

		parsed.Should().BeTrue();
		var info = message.Info!.Value.Payload;
		info.Depth.Should().BeNull();
		info.PrincipalVariation.Should().BeNull();
	}

	[Fact]
	public void TryParse_WhenInfoTokensUseTabs_ShouldParseVariation()
	{
		bool parsed = UciProtocolParser.TryParse("info\tdepth\t12\tpv\te2e4", out var value);

		parsed.Should().BeTrue();
		value.Info!.Value.Payload.PrincipalVariation!.Value.Moves.Should().Equal("e2e4");
	}

	[Fact]
	public void TryParse_WhenFieldsFollowPv_ShouldKeepThemInTheTerminalMoveSequence()
	{
		bool parsed = UciProtocolParser.TryParse("info depth 12 pv e2e4 depth 99", out var message);

		parsed.Should().BeTrue();
		var info = message.Info!.Value.Payload;
		info.Depth.Should().Be(12u);
		info.PrincipalVariation!.Value.Moves.Should().Equal("e2e4", "depth", "99");
	}

	[Theory]
	[InlineData("info score cp 40 score mate -2", null, -2, UciScoreBound.Exact)]
	[InlineData("info score mate 2 score CP 40 UPPERBOUND", 40, null, UciScoreBound.Upper)]
	[InlineData("info score cp 40 score unknown 12", null, null, UciScoreBound.Exact)]
	[InlineData("info score cp 40 score cp invalid", null, null, UciScoreBound.Exact)]
	[InlineData("info score cp 40 score", 40, null, UciScoreBound.Exact)]
	public void TryParse_WhenScoreClausesRepeat_ShouldUseLastCompleteClause(
		string line,
		int? expectedCentipawns,
		int? expectedMate,
		UciScoreBound expectedBound)
	{
		bool parsed = UciProtocolParser.TryParse(line, out var message);

		parsed.Should().BeTrue();
		message.Info!.Value.Payload.Score.Should().Be(
			new UciInfoScore(expectedCentipawns, expectedMate, expectedBound)
		);
	}

	[Fact]
	public void TryParse_WhenStringContainsProtocolKeywords_ShouldTreatStringAsTerminal()
	{
		bool parsed = UciProtocolParser.TryParse("info depth 12 string pv e2e4", out var message);

		parsed.Should().BeTrue();
		var info = message.Info!.Value.Payload;
		info.String.Should().Be("pv e2e4");
		info.PrincipalVariation.Should().BeNull();
	}

	[Fact]
	public void TryParse_WhenOptionLineIsProvided_ShouldReturnTypedMessage()
	{
		bool parsed = UciProtocolParser.TryParse(
			"option name Style type combo default Normal var Normal var Aggressive",
			out var message
		);

		parsed.Should().BeTrue();
		message.Option.HasValue.Should().BeTrue();
		var option = message.Option!.Value.Option;
		option.Name.Should().Be("Style");
		option.Type.Should().Be(UciOptionType.Combo);
		option.DefaultValue.Should().Be("Normal");
		option.Variables.Should().Equal("Normal", "Aggressive");
	}

	[Fact]
	public void TryParse_WhenReadyOkLineIsProvided_ShouldReturnTypedMessage()
	{
		bool parsed = UciProtocolParser.TryParse("readyok", out var message);

		parsed.Should().BeTrue();
		message.ReadyOk.HasValue.Should().BeTrue();
	}

	[Fact]
	public void TryParse_WhenRegistrationLineIsProvided_ShouldReturnTypedMessage()
	{
		bool parsed = UciProtocolParser.TryParse("registration error", out var message);

		parsed.Should().BeTrue();
		message.Registration.HasValue.Should().BeTrue();
		message.Registration!.Value.State.Should().Be(UciProtectionState.Error);
	}

	[Fact]
	public void TryParse_WhenUciOkLineIsProvided_ShouldReturnTypedMessage()
	{
		bool parsed = UciProtocolParser.TryParse("uciok", out var message);

		parsed.Should().BeTrue();
		message.UciOk.HasValue.Should().BeTrue();
	}
}

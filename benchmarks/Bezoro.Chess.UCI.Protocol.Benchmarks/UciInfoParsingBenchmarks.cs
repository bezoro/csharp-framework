using System;
using BenchmarkDotNet.Attributes;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;

namespace Bezoro.Chess.UCI.Protocol.Benchmarks;

[MemoryDiagnoser]
public class UciInfoParsingBenchmarks
{
	private const string RICH_INFO_LINE =
		"info depth 20 seldepth 32 multipv 2 score cp 34 lowerbound nodes 12345 nps 456789 hashfull 12 cpuload 876 time 250 tbhits 4 currmove e2e4 currmovenumber 7 refutation d2d4 d7d5 currline 1 e2e4 e7e5 g1f3 pv e2e4 e7e5 g1f3";

	private const string WDL_INFO_LINE =
		"info depth 20 seldepth 28 multipv 1 score cp 33 wdl 70 910 20 nodes 248123 nps 761325 hashfull 12 tbhits 0 time 326 pv e2e4 e7e5 g1f3 b8c6 f1b5 g8f6 e1g1 f6e4 d2d4 e4d6";

	[Benchmark(Description = "Parse WDL and ten-move PV as typed UCI message")]
	public int ParseWdlTenMoves() =>
		UciProtocolParser.TryParse(WDL_INFO_LINE, out var message)
			? message.Info?.Payload.PrincipalVariation?.Moves.Length ?? 0
			: 0;

	[GlobalSetup]
	public void ValidateInput()
	{
		if (ParseWdlTenMoves() != 10) throw new InvalidOperationException("Expected a ten-move WDL report.");
		if (ParseRichInfo_ProtocolParser() != 3 || ParseRichInfo_PrincipalVariation() != 3)
			throw new InvalidOperationException("The benchmark input must produce a three-move principal variation.");
	}

	[Benchmark(Description = "Parse rich info as typed UCI message")]
	public int ParseRichInfo_ProtocolParser()
	{
		if (!UciProtocolParser.TryParse(RICH_INFO_LINE, out var message) || !message.Info.HasValue)
			return 0;

		return message.Info.Value.Payload.PrincipalVariation?.Moves.Length ?? 0;
	}

	[Benchmark(Description = "Parse rich info as principal variation")]
	public int ParseRichInfo_PrincipalVariation() =>
		PrincipalVariation.TryParse(RICH_INFO_LINE, out var principalVariation)
			? principalVariation.Moves.Length
			: 0;
}

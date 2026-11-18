using System.Text;
using System.Collections.Immutable;
using System.Globalization;

namespace Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;

internal static class UciInfoParser
{
	public static bool TryParseTrimmed(string line, out UciInfoPayload payload, bool captureDetails = true)
	{
		payload = default;
		var tokens = new UciTokens(line.AsSpan());
		if (!tokens.Consume("info") || tokens.Remaining.IsEmpty) return false;

		uint? depth = null;
		uint? selDepth = null;
		uint? multiPv = null;
		ulong? nodes = null;
		ulong? nps = null;
		ulong? tbHits = null;
		ulong? time = null;
		uint? hashFull = null;
		uint? cpuLoad = null;
		string? currentMove = null;
		uint? currentMoveNumber = null;
		uint? currentLineCpu = null;
		string? infoString = null;
		UciInfoScore? score = null;
		UciWdl? wdl = null;
		var refutation = ImmutableArray<string>.Empty;
		var currentLine = ImmutableArray<string>.Empty;
		PrincipalVariation? principalVariation = null;

		while (!tokens.Remaining.IsEmpty)
		{
			switch (tokens.Read())
			{
				case "depth" when TryReadUInt(ref tokens, out uint depthValue):
					depth = depthValue;
					break;
				case "seldepth" when TryReadUInt(ref tokens, out uint selDepthValue):
					selDepth = selDepthValue;
					break;
				case "multipv" when TryReadUInt(ref tokens, out uint multiPvValue):
					multiPv = multiPvValue;
					break;
				case "nodes" when TryReadULong(ref tokens, out ulong nodesValue):
					nodes = nodesValue;
					break;
				case "nps" when TryReadULong(ref tokens, out ulong npsValue):
					nps = npsValue;
					break;
				case "tbhits" when TryReadULong(ref tokens, out ulong tbHitsValue):
					tbHits = tbHitsValue;
					break;
				case "time" when TryReadULong(ref tokens, out ulong timeValue):
					time = timeValue;
					break;
				case "hashfull" when TryReadUInt(ref tokens, out uint hashFullValue):
					hashFull = hashFullValue;
					break;
				case "cpuload" when TryReadUInt(ref tokens, out uint cpuLoadValue):
					cpuLoad = cpuLoadValue;
					break;
				case "currmove" when !tokens.Remaining.IsEmpty:
					var move = tokens.Read();
					if (captureDetails) currentMove = move.ToString();
					break;
				case "currmovenumber" when TryReadUInt(ref tokens, out uint currentMoveNumberValue):
					currentMoveNumber = currentMoveNumberValue;
					break;
				case "score":
					var scoreTokens = tokens;
					var kind = scoreTokens.Read();
					var value = scoreTokens.Read();
					if (value.IsEmpty) break;
					int? cp = null, mate = null;
					if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
					{
						if (kind.Equals("cp", StringComparison.OrdinalIgnoreCase)) cp = numeric;
						else if (kind.Equals("mate", StringComparison.OrdinalIgnoreCase)) mate = numeric;
					}
					var bound = UciScoreBound.Exact;
					if (scoreTokens.Consume("lowerbound")) bound = UciScoreBound.Lower;
					else if (scoreTokens.Consume("upperbound")) bound = UciScoreBound.Upper;
					score = new(cp, mate, bound);
					tokens = scoreTokens;
					break;
				case "wdl":
					var wdlTokens = tokens;
					if (TryReadUInt(ref wdlTokens, out uint wins) && wins <= 1000 &&
						TryReadUInt(ref wdlTokens, out uint draws) && draws <= 1000 &&
						TryReadUInt(ref wdlTokens, out uint losses) && losses <= 1000 &&
						wins + draws + losses == 1000)
					{
						wdl = new((int)wins, (int)draws, (int)losses);
						tokens = wdlTokens;
					}
					break;
				case "refutation":
					refutation = ReadSequence(ref tokens, stopAtKeyword: true, retain: captureDetails);
					break;
				case "currline":
					if (TryReadUInt(ref tokens, out uint cpu)) currentLineCpu = cpu;
					currentLine = ReadSequence(ref tokens, stopAtKeyword: true, retain: captureDetails);
					break;
				case "string":
					if (captureDetails) infoString = ReadText(tokens);
					tokens = default;
					break;
				case "pv":
					var pvTokens = tokens;
					var moves = ReadSequence(ref tokens, stopAtKeyword: false, retain: true);
					if (!moves.IsEmpty)
						principalVariation = new(depth ?? 0, selDepth ?? 0, multiPv ?? 0, score?.Centipawns,
							score?.Mate, nodes ?? 0, nps ?? 0, tbHits ?? 0, time ?? 0, moves, ReadText(pvTokens))
							{ Wdl = wdl, ScoreBound = score?.Bound ?? UciScoreBound.Exact };
					break;
			}
		}

		payload = new(
			depth,
			selDepth,
			multiPv,
			score,
			nodes,
			nps,
			tbHits,
			time,
			hashFull,
			cpuLoad,
			currentMove,
			currentMoveNumber,
			refutation,
			currentLineCpu,
			currentLine,
			infoString,
			principalVariation
		) { Wdl = wdl };

		return true;
	}

	private static bool TryReadULong(ref UciTokens tokens, out ulong value)
	{
		var next = tokens;
		if (!ulong.TryParse(next.Read(), NumberStyles.None, CultureInfo.InvariantCulture, out value)) return false;
		tokens = next;
		return true;
	}

	private static bool TryReadUInt(ref UciTokens tokens, out uint value)
	{
		var next = tokens;
		if (!uint.TryParse(next.Read(), NumberStyles.None, CultureInfo.InvariantCulture, out value)) return false;
		tokens = next;
		return true;
	}

	private static ImmutableArray<string> ReadSequence(ref UciTokens tokens, bool stopAtKeyword, bool retain)
	{
		var start = tokens;
		int count = 0;
		while (!tokens.Remaining.IsEmpty)
		{
			var next = tokens;
			var token = next.Read();
			if (stopAtKeyword && IsInfoKeyword(token)) break;
			tokens = next;
			count++;
		}
		if (!retain || count == 0) return ImmutableArray<string>.Empty;
		var moves = ImmutableArray.CreateBuilder<string>(count);
		for (int i = 0; i < count; i++) moves.Add(start.Read().ToString());
		return moves.MoveToImmutable();
	}

	private static string ReadText(UciTokens tokens)
	{
		// Preserve the public normalized text without building another array of move strings.
		var text = tokens.Remaining.TrimEnd();
		for (int i = 0; i < text.Length; i++)
		{
			if (!char.IsWhiteSpace(text[i]) || (text[i] == ' ' && text[i - 1] != ' ')) continue;
			var builder = new StringBuilder(text.Length);
			while (!tokens.Remaining.IsEmpty)
			{
				if (builder.Length > 0) builder.Append(' ');
				builder.Append(tokens.Read());
			}
			return builder.ToString();
		}
		return text.ToString();
	}

	private static bool IsInfoKeyword(ReadOnlySpan<char> token) =>
		token is
			"depth" or
			"seldepth" or
			"multipv" or
			"score" or
			"wdl" or
			"nodes" or
			"nps" or
			"tbhits" or
			"time" or
			"hashfull" or
			"cpuload" or
			"currmove" or
			"currmovenumber" or
			"refutation" or
			"currline" or
			"pv" or
			"string";
}

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;

namespace Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;

internal static class UciInfoParser
{
	public static bool TryParseTrimmed(string line, out UciInfoPayload payload)
	{
		payload = default;
		if (!line.StartsWith("info ", StringComparison.OrdinalIgnoreCase))
			return false;

		string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (tokens.Length < 2) return false;

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
		List<string>? refutation = null;
		List<string>? currentLine = null;
		PrincipalVariation? principalVariation = null;

		for (var i = 1; i < tokens.Length; i++)
		{
			switch (tokens[i])
			{
				case "depth" when TryReadUInt(tokens, i + 1, out uint depthValue):
					depth = depthValue;
					i++;
					break;
				case "seldepth" when TryReadUInt(tokens, i + 1, out uint selDepthValue):
					selDepth = selDepthValue;
					i++;
					break;
				case "multipv" when TryReadUInt(tokens, i + 1, out uint multiPvValue):
					multiPv = multiPvValue;
					i++;
					break;
				case "nodes" when TryReadULong(tokens, i + 1, out ulong nodesValue):
					nodes = nodesValue;
					i++;
					break;
				case "nps" when TryReadULong(tokens, i + 1, out ulong npsValue):
					nps = npsValue;
					i++;
					break;
				case "tbhits" when TryReadULong(tokens, i + 1, out ulong tbHitsValue):
					tbHits = tbHitsValue;
					i++;
					break;
				case "time" when TryReadULong(tokens, i + 1, out ulong timeValue):
					time = timeValue;
					i++;
					break;
				case "hashfull" when TryReadUInt(tokens, i + 1, out uint hashFullValue):
					hashFull = hashFullValue;
					i++;
					break;
				case "cpuload" when TryReadUInt(tokens, i + 1, out uint cpuLoadValue):
					cpuLoad = cpuLoadValue;
					i++;
					break;
				case "currmove" when i + 1 < tokens.Length:
					currentMove = tokens[i + 1];
					i++;
					break;
				case "currmovenumber" when TryReadUInt(tokens, i + 1, out uint currentMoveNumberValue):
					currentMoveNumber = currentMoveNumberValue;
					i++;
					break;
				case "score" when i + 2 < tokens.Length:
					{
						string type = tokens[i + 1];
						string rawValue = tokens[i + 2];
						i += 2;

						int? centipawns = null;
						int? mate = null;
						if (type.Equals("cp", StringComparison.OrdinalIgnoreCase) &&
							int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cpValue))
							centipawns = cpValue;
						else if (type.Equals("mate", StringComparison.OrdinalIgnoreCase) &&
								 int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mateValue))
							mate = mateValue;

						var bound = UciScoreBound.Exact;
						if (i + 1 < tokens.Length)
						{
							if (tokens[i + 1].Equals("lowerbound", StringComparison.OrdinalIgnoreCase))
							{
								bound = UciScoreBound.Lower;
								i++;
							}
							else if (tokens[i + 1].Equals("upperbound", StringComparison.OrdinalIgnoreCase))
							{
								bound = UciScoreBound.Upper;
								i++;
							}
						}

						score = new(centipawns, mate, bound);
						break;
					}
				case "wdl" when TryReadUInt(tokens, i + 1, out uint wins) && wins <= 1000 &&
					TryReadUInt(tokens, i + 2, out uint draws) && draws <= 1000 &&
					TryReadUInt(tokens, i + 3, out uint losses) && losses <= 1000 &&
					wins + draws + losses == 1000:
					wdl = new((int)wins, (int)draws, (int)losses);
					i += 3;
					break;
				case "refutation":
					refutation = ReadTokenSequence(tokens, ref i);
					break;
				case "currline":
					{
						int startIndex = i + 1;
						if (TryReadUInt(tokens, startIndex, out uint cpuValue))
						{
							currentLineCpu = cpuValue;
							startIndex++;
						}

						currentLine = ReadTokenSequence(tokens, ref i, startIndex);
						break;
					}
				case "string":
					infoString = string.Join(" ", tokens, i + 1, tokens.Length - (i + 1));
					i = tokens.Length;
					break;
				case "pv":
					{
						var pvMoves = new List<string>();
						for (int j = i + 1; j < tokens.Length; j++)
							pvMoves.Add(tokens[j]);

						if (pvMoves.Count > 0)
							principalVariation = new(
								depth ?? 0,
								selDepth ?? 0,
								multiPv ?? 0,
								score?.Centipawns,
								score?.Mate,
								nodes ?? 0,
								nps ?? 0,
								tbHits ?? 0,
								time ?? 0,
								pvMoves.ToImmutableArray(),
								string.Join(" ", pvMoves)
							) { Wdl = wdl, ScoreBound = score?.Bound ?? UciScoreBound.Exact };

						i = tokens.Length;
						break;
					}
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
			refutation is { Count: > 0 } ? refutation.ToImmutableArray() : ImmutableArray<string>.Empty,
			currentLineCpu,
			currentLine is { Count: > 0 } ? currentLine.ToImmutableArray() : ImmutableArray<string>.Empty,
			infoString,
			principalVariation
		) { Wdl = wdl };

		return true;
	}

	private static bool TryReadULong(string[] tokens, int index, out ulong value)
	{
		value = 0;
		return index < tokens.Length && ulong.TryParse(tokens[index], NumberStyles.None, CultureInfo.InvariantCulture, out value);
	}

	private static bool TryReadUInt(string[] tokens, int index, out uint value)
	{
		value = 0;
		return index < tokens.Length && uint.TryParse(tokens[index], NumberStyles.None, CultureInfo.InvariantCulture, out value);
	}

	private static List<string> ReadTokenSequence(string[] tokens, ref int currentIndex) =>
		ReadTokenSequence(tokens, ref currentIndex, currentIndex + 1);

	private static List<string> ReadTokenSequence(string[] tokens, ref int currentIndex, int startIndex)
	{
		var values = new List<string>();
		int index = startIndex;
		while (index < tokens.Length && !IsInfoKeyword(tokens[index]))
		{
			values.Add(tokens[index]);
			index++;
		}

		currentIndex = index - 1;
		return values;
	}

	private static bool IsInfoKeyword(string token) =>
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

using System.Collections.Immutable;
using Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;

namespace Bezoro.Chess.UCI.Protocol.API.Types;

/// <summary>
///     Represents a principal variation in a chess position evaluation.
/// </summary>
/// <param name="Depth">The depth of this principal variation</param>
/// <param name="SelDepth">The selective depth of this principal variation</param>
/// <param name="MultiPv">The MultiPv value of this principal variation</param>
/// <param name="ScoreCp">The centipawn score of this principal variation, or null if not available</param>
/// <param name="ScoreMate">The mate score of this principal variation, or null if not available</param>
/// <param name="Nodes">The number of nodes searched for this principal variation</param>
/// <param name="Nps">The nodes per second for this principal variation</param>
/// <param name="TbHits">The number of tablebase hits for this principal variation</param>
/// <param name="Time">The time taken for this principal variation in milliseconds</param>
/// <param name="Moves">The sequence of moves in this principal variation</param>
/// <param name="RawPv">The raw PV move string as emitted by the engine.</param>
/// <remarks>
///     Parsing uses the same UCI <c>info</c> grammar as the typed protocol stream. The <c>info</c> prefix, score
///     kinds, and score bounds are case-insensitive; field names remain lowercase UCI tokens.
/// </remarks>
public readonly record struct PrincipalVariation(
	uint                  Depth,
	uint                  SelDepth,
	uint                  MultiPv,
	int?                  ScoreCp,
	int?                  ScoreMate,
	ulong                 Nodes,
	ulong                 Nps,
	ulong                 TbHits,
	ulong                 Time,
	ImmutableArray<string> Moves,
	string                RawPv
)
{
	/// <summary>Gets optional win/draw/loss estimates from the engine, relative to the side to move.</summary>
	public UciWdl? Wdl { get; init; }

	/// <summary>Gets whether the reported score is exact or a search bound.</summary>
	public UciScoreBound ScoreBound { get; init; }

	/// <summary>
	///     Attempts to parse a UCI <c>info ... pv ...</c> line.
	/// </summary>
	/// <param name="line">Raw engine output line.</param>
	/// <param name="pv">Parsed principal variation when successful.</param>
	/// <returns><see langword="true" /> when the line contains a valid PV payload; otherwise <see langword="false" />.</returns>
	public static bool TryParse(string line, out PrincipalVariation pv)
	{
		pv = new(0, 0, 0, null, null, 0, 0, 0, 0, ImmutableArray<string>.Empty, string.Empty);
		if (string.IsNullOrWhiteSpace(line) ||
			!UciInfoParser.TryParseTrimmed(line, out var info, captureDetails: false) ||
			info.PrincipalVariation is not { } parsed)
			return false;

		pv = parsed;
		return true;
	}
}

using Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;

namespace Bezoro.Chess.UCI.API.Types;

/// <summary>
///     Represents either a centipawn evaluation or a distance-to-mate score.
/// </summary>
public readonly record struct MoveScore()
{
	private MoveScore(int? scoreCp, int? scoreMate) : this()
	{
		ScoreCp   = scoreCp;
		ScoreMate = scoreMate;
	}

	/// <summary>Gets the centipawn score, when available.</summary>
	public int? ScoreCp { get; }

	/// <summary>Gets the signed number of moves to mate, when available.</summary>
	public int? ScoreMate { get; }

	/// <summary>
	///     Attempts to parse a score from a UCI engine information line.
	/// </summary>
	/// <param name="line">The UCI information line.</param>
	/// <param name="score">When successful, receives the parsed score.</param>
	/// <returns><see langword="true" /> when a centipawn or mate score is present and valid; otherwise, <see langword="false" />.</returns>
	public static bool TryParse(string line, out MoveScore? score)
	{
		score = null;
		if (string.IsNullOrWhiteSpace(line) ||
			!UciInfoParser.TryParseTrimmed(line.Trim(), out var info) ||
			info.Score is not { } parsed ||
			!parsed.Centipawns.HasValue && !parsed.Mate.HasValue)
			return false;

		score = new(parsed.Centipawns, parsed.Mate);
		return true;
	}

	/// <summary>Creates a centipawn score.</summary>
	/// <param name="cp">The signed centipawn evaluation.</param>
	/// <returns>A centipawn-based score.</returns>
	public static MoveScore FromCp(int cp) => new(cp, null);

	/// <summary>Creates a distance-to-mate score.</summary>
	/// <param name="mate">The signed number of moves to mate.</param>
	/// <returns>A mate-based score.</returns>
	public static MoveScore FromMate(int mate) => new(null, mate);

	/// <summary>
	///     Builds a MoveScore from a SearchResult returned by the engine.
	///     Prefers mate scores when present, otherwise falls back to centipawns.
	/// </summary>
	/// <param name="result">The engine search result to convert.</param>
	/// <returns>A mate or centipawn score, or the default value when no score is available.</returns>
	public static MoveScore FromSearchResult(SearchResult result)
	{
		if (result.HasMate && result.MateScore.HasValue)
			return FromMate(result.MateScore.Value);

		int? cp = result.BestCpScore;
		if (!cp.HasValue)
		{
			var variations = result.PrincipalVariations;
			if (!variations.IsDefaultOrEmpty)
				// Prefer the first available centipawn score
				foreach (var pv in variations)
				{
					if (pv.ScoreCp.HasValue)
					{
						cp = pv.ScoreCp;
						break;
					}
				}
		}

		return cp.HasValue ? FromCp(cp.Value) : default;
	}
}

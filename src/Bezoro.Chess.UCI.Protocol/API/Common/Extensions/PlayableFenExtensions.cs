using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.Chess.UCI.Protocol.Internal;

namespace Bezoro.Chess.UCI.Protocol.API.Common.Extensions;

/// <summary>Validates positions used by playable sessions and game review.</summary>
public static class PlayableFenExtensions
{
	/// <summary>Rejects positions that violate basic playable-position constraints without contacting an engine.</summary>
	/// <remarks>
	/// Checks kings, pawn ranks, piece counts, castling prerequisites, en-passant consistency,
	/// and that the inactive king is not in check. This does not prove historical reachability from the starting position.
	/// FEN parsing itself remains syntactic so editors can represent incomplete positions.
	/// </remarks>
	/// <param name="fen">Syntactically parsed position to validate.</param>
	/// <exception cref="ArgumentException">The position violates a playable-position constraint.</exception>
	public static void ValidateForPlay(this Fen fen) => PlayablePositionValidator.Validate(fen);
}

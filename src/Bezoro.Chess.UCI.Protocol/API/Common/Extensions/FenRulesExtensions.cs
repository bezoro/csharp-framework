using System.Collections.Immutable;
using Bezoro.Chess.UCI.Protocol.Internal;

namespace Bezoro.Chess.UCI.Protocol.API.Common.Extensions;

/// <summary>
///     Extension methods for deterministic local chess rules derived from a FEN.
/// </summary>
public static class FenRulesExtensions
{
	/// <summary>Returns whether the side to move is in check, using local rules without an engine.</summary>
	public static bool IsInCheck(this Fen fen) => LocalPositionRules.IsCurrentPlayerInCheck(fen);

	/// <summary>
	///     Applies a legal UCI move locally and returns the resulting FEN.
	/// </summary>
	/// <param name="fen">Current position.</param>
	/// <param name="move">Legal move in UCI notation.</param>
	/// <returns>Resulting FEN after the move is applied.</returns>
	public static Fen ApplyMove(this Fen fen, string move) => LocalPositionRules.ApplyMove(fen, move);

	/// <summary>
	///     Enumerates all legal UCI moves for the current side to move.
	/// </summary>
	/// <param name="fen">Current position.</param>
	/// <returns>All legal moves in lowercase UCI notation.</returns>
	public static ImmutableArray<string> GetLegalMoves(this Fen fen) => LocalPositionRules.GetLegalMoves(fen);
}

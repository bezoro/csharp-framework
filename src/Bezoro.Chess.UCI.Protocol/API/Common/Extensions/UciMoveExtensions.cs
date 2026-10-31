using System.Collections.Generic;
using System.Collections.Immutable;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.UCI.Protocol.API.Common.Extensions;

/// <summary>
///     Extension methods for working with UCI move collections.
/// </summary>
public static class UciMoveExtensions
{
	/// <summary>
	///     Returns true when the move collection contains the supplied UCI move with ordinal matching.
	/// </summary>
	/// <param name="moves">Move sequence to search.</param>
	/// <param name="move">Move in UCI notation.</param>
	public static bool ContainsUciMove(this IEnumerable<string> moves, string move)
	{
		if (moves is null) throw new ArgumentNullException(nameof(moves));
		if (move is null) throw new ArgumentNullException(nameof(move));

		foreach (string candidate in moves)
		{
			if (string.Equals(candidate, move, StringComparison.Ordinal))
				return true;
		}

		return false;
	}

	/// <summary>
	///     Normalizes a move sequence to lowercase invariant UCI strings.
	/// </summary>
	/// <param name="moves">Move sequence to normalize.</param>
	/// <returns>Immutable normalized snapshot.</returns>
	public static ImmutableArray<string> NormalizeUciMoves(this IEnumerable<string> moves)
	{
		if (moves is null) throw new ArgumentNullException(nameof(moves));

		var builder = ImmutableArray.CreateBuilder<string>();
		foreach (string move in moves)
		{
			if (move is null || !UciEngineClient.IsUciMoveString(move.Trim()))
				throw new ArgumentException("Every move must be valid UCI coordinate notation.", nameof(moves));
			builder.Add(move.Trim().ToLowerInvariant());
		}

		return builder.ToImmutable();
	}

	/// <summary>
	///     Attempts to parse a single strict UCI coordinate move.
	/// </summary>
	/// <param name="move">Candidate move such as <c>e2e4</c> or <c>a7a8q</c>.</param>
	/// <param name="coordinateMove">Parsed coordinate move when successful.</param>
	/// <returns><see langword="true" /> when the move has valid squares and a valid promotion suffix.</returns>
	public static bool TryParseCoordinateMove(this string? move, out UciCoordinateMove coordinateMove) =>
		UciCoordinateMove.TryParse(move, out coordinateMove);

	/// <summary>
	///     Attempts to normalize a single strict UCI coordinate move.
	/// </summary>
	/// <param name="move">Candidate move such as <c>e2e4</c> or <c>a7a8q</c>.</param>
	/// <param name="normalizedMove">Lowercase normalized move when successful.</param>
	/// <returns><see langword="true" /> when the move has valid squares and a valid promotion suffix.</returns>
	public static bool TryNormalizeCoordinateMove(this string? move, out string normalizedMove) =>
		UciCoordinateMove.TryNormalize(move, out normalizedMove);
}

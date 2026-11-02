using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.UCI.Protocol.Internal;

internal static class PlayablePositionValidator
{
	internal static void Validate(Fen fen)
	{
		var state = LocalPositionRules.Parse(fen);
		var counts = new int[2];
		var pawns = new int[2];
		int whiteKing = -1, blackKing = -1;
		for (var square = 0; square < state.Board.Length; square++)
		{
			char piece = state.Board[square];
			if (piece == '\0') continue;
			int side = char.IsUpper(piece) ? 0 : 1;
			counts[side]++;
			if (char.ToLowerInvariant(piece) == 'p')
			{
				if (LocalPositionRules.GetRank(square) is 0 or 7)
					throw new ArgumentException("Pawns cannot occupy the first or eighth rank in a playable position.", nameof(fen));
				pawns[side]++;
			}
			if (piece == 'K')
			{
				if (whiteKing >= 0) throw new ArgumentException("Exactly one king per side is required.", nameof(fen));
				whiteKing = square;
			}
			if (piece == 'k')
			{
				if (blackKing >= 0) throw new ArgumentException("Exactly one king per side is required.", nameof(fen));
				blackKing = square;
			}
		}
		if (whiteKing < 0 || blackKing < 0 || counts[0] > 16 || counts[1] > 16 || pawns[0] > 8 || pawns[1] > 8)
			throw new ArgumentException("A playable position requires two kings and at most sixteen pieces and eight pawns per side.", nameof(fen));

		foreach (char right in state.CastlingRights)
		{
			int kingSquare = char.IsUpper(right) ? 4 : 60;
			int rookSquare = right switch { 'K' => 7, 'Q' => 0, 'k' => 63, _ => 56 };
			if (state.Board[kingSquare] != (char.IsUpper(right) ? 'K' : 'k') ||
				state.Board[rookSquare] != (char.IsUpper(right) ? 'R' : 'r'))
				throw new ArgumentException("Castling rights require the king and rook on their original squares.", nameof(fen));
		}
		if (state.EnPassantTarget.Length > 0)
		{
			int target = LocalPositionRules.ParseSquare(state.EnPassantTarget.AsSpan());
			int direction = state.ActiveColor == 'w' ? 8 : -8;
			if (LocalPositionRules.GetRank(target) != (state.ActiveColor == 'w' ? 5 : 2) ||
				state.Board[target] != '\0' ||
				state.Board[target - direction] != (state.ActiveColor == 'w' ? 'p' : 'P') ||
				state.Board[target + direction] != '\0' || state.HalfmoveClock != 0)
				throw new ArgumentException("The en-passant target must describe the preceding double pawn push.", nameof(fen));
		}

		var inactiveSide = Fen.Parse($"{fen.PiecePlacement} {LocalPositionRules.Opposite(fen.ActiveColor)} - - 0 1")!.Value;
		if (LocalPositionRules.IsCurrentPlayerInCheck(inactiveSide))
			throw new ArgumentException("The side that is not moving cannot already be in check. Kings must not be adjacent.", nameof(fen));
	}

}

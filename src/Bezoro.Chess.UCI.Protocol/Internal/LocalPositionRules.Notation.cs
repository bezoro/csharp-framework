using System.Text;

namespace Bezoro.Chess.UCI.Protocol.Internal;

internal static partial class LocalPositionRules
{
	internal static LocalPositionState Parse(Fen fen)
	{
		var board = new char[64];
		ReadBoard(fen, board);
		return new(
			board,
			fen.ActiveColor,
			NormalizeCastlingRights(fen.CastlingRights),
			NormalizeEnPassantTarget(fen.EnPassantTarget),
			fen.HalfmoveClock,
			fen.FullmoveNumber
		);
	}

	private static void ReadBoard(Fen fen, Span<char> board)
	{
		if (fen.PiecePlacement is null || fen.ActiveColor is not ('w' or 'b'))
			throw new ArgumentException("A parsed FEN position is required.", nameof(fen));
		board.Clear();
		var rank = 7;
		var file = 0;
		foreach (char symbol in fen.PiecePlacement)
		{
			if (symbol == '/')
			{
				rank--;
				file = 0;
			}
			else if (symbol is >= '1' and <= '8')
				file += symbol - '0';
			else
				board[GetIndex(file++, rank)] = symbol;
		}
	}

	internal static string NormalizeMove(string move)
	{
		if (string.IsNullOrWhiteSpace(move))
			throw new ArgumentException("Move must not be blank.", nameof(move));

		string normalizedMove = move.Trim().ToLowerInvariant();
		if (!UciEngineClient.IsUciMoveString(normalizedMove))
			throw new ArgumentException("Move must be valid UCI notation.", nameof(move));

		return normalizedMove;
	}

	internal static int ParseSquare(ReadOnlySpan<char> square) => GetIndex(square[0] - 'a', square[1] - '1');

	internal static string FormatMove(int fromIndex, int toIndex, char promotionPiece = '\0') =>
		string.Create(promotionPiece == '\0' ? 4 : 5, (fromIndex, toIndex, promotionPiece), static (chars, state) =>
		{
			chars[0] = (char)('a' + GetFile(state.fromIndex));
			chars[1] = (char)('1' + GetRank(state.fromIndex));
			chars[2] = (char)('a' + GetFile(state.toIndex));
			chars[3] = (char)('1' + GetRank(state.toIndex));
			if (chars.Length == 5)
				chars[4] = state.promotionPiece;
		});

	internal static string FormatSquare(int index) =>
		string.Create(2, index, static (chars, square) =>
		{
			chars[0] = (char)('a' + GetFile(square));
			chars[1] = (char)('1' + GetRank(square));
		});

	internal static int GetIndex(int file, int rank) => rank * 8 + file;

	internal static int GetFile(int index) => index % 8;

	internal static int GetRank(int index) => index / 8;

	internal static bool IsOnBoard(int file, int rank) => file is >= 0 and < 8 && rank is >= 0 and < 8;

	internal static string NormalizeCastlingRights(string castlingRights) =>
		string.Equals(castlingRights, "-", StringComparison.Ordinal) ? string.Empty : castlingRights;

	internal static string NormalizeEnPassantTarget(string enPassantTarget) =>
		string.Equals(enPassantTarget, "-", StringComparison.Ordinal)
			? string.Empty
			: enPassantTarget.ToLowerInvariant();

	private static string BuildRawFen(LocalPositionState state)
	{
		var builder = new StringBuilder(96);
		for (var rank = 7; rank >= 0; rank--)
		{
			var emptyCount = 0;
			for (var file = 0; file < 8; file++)
			{
				char piece = state.Board[GetIndex(file, rank)];
				if (piece == '\0')
				{
					emptyCount++;
					continue;
				}

				if (emptyCount > 0)
				{
					builder.Append(emptyCount);
					emptyCount = 0;
				}

				builder.Append(piece);
			}

			if (emptyCount > 0)
				builder.Append(emptyCount);

			if (rank > 0)
				builder.Append('/');
		}

		builder.Append(' ');
		builder.Append(state.ActiveColor);
		builder.Append(' ');
		builder.Append(string.IsNullOrEmpty(state.CastlingRights) ? "-" : state.CastlingRights);
		builder.Append(' ');
		builder.Append(string.IsNullOrEmpty(state.EnPassantTarget) ? "-" : state.EnPassantTarget);
		builder.Append(' ');
		builder.Append(state.HalfmoveClock);
		builder.Append(' ');
		builder.Append(state.FullmoveNumber);

		return builder.ToString();
	}
}

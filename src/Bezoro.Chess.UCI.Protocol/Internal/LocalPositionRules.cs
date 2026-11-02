using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace Bezoro.Chess.UCI.Protocol.Internal;

internal static partial class LocalPositionRules
{
	internal static Fen ApplyMove(Fen fen, string move)
	{
		string normalizedMove = NormalizeMove(move);
		var state = Parse(fen);
		if (!IsLegalCandidate(state, normalizedMove))
			throw new InvalidOperationException("The move is not legal in the supplied FEN position.");

		var structural = ClassifyNormalized(state, normalizedMove);
		var next = ApplyMove(state, normalizedMove, structural);
		return Fen.Parse(BuildRawFen(next))!.Value;
	}

	internal static Fen ApplyAndClassifyMove(
		Fen fen,
		string move,
		out MoveClassification classification,
		out ImmutableArray<string> resultingLegalMoves)
	{
		string normalizedMove = NormalizeMove(move);
		var state = Parse(fen);
		if (!IsLegalCandidate(state, normalizedMove))
			throw new InvalidOperationException("The move is not legal in the supplied FEN position.");
		var structural = ClassifyNormalized(state, normalizedMove);
		var next = ApplyMove(state, normalizedMove, structural);
		resultingLegalMoves = GenerateLegalMoves(next);
		bool isCheck = IsKingInCheck(next.Board, next.ActiveColor);
		classification = structural.WithTacticalOutcome(isCheck,
			isCheck && resultingLegalMoves.IsEmpty, !isCheck && resultingLegalMoves.IsEmpty);
		return Fen.Parse(BuildRawFen(next))!.Value;
	}

	internal static ImmutableArray<string> GetLegalMoves(Fen fen) => GenerateLegalMoves(Parse(fen));

	internal static bool IsCurrentPlayerInCheck(Fen fen)
	{
		Span<char> board = stackalloc char[64];
		ReadBoard(fen, board);
		return IsKingInCheck(board, fen.ActiveColor);
	}

	internal static bool TryCreatePendingPromotion(
		Fen                    fen,
		string                 move,
		ImmutableArray<string> legalMoves,
		out PendingPromotionRequest request)
	{
		request = default;
		if (string.IsNullOrWhiteSpace(move))
			return false;

		string normalizedMove = move.Trim().ToLowerInvariant();
		if (normalizedMove.Length != 4 || !UciEngineClient.IsUciMoveString(normalizedMove + "q"))
			return false;

		var promotionChoices = "qrbn"
			.Where(candidate => legalMoves.Contains(normalizedMove + candidate, StringComparer.Ordinal))
			.ToImmutableArray();

		if (promotionChoices.IsDefaultOrEmpty)
			return false;

		var state = Parse(fen);
		int fromIndex = ParseSquare(normalizedMove.AsSpan(0, 2));
		char movingPiece = state.Board[fromIndex];

		request = new(
			normalizedMove,
			fen.ActiveColor,
			movingPiece,
			normalizedMove[..2],
			normalizedMove.Substring(2, 2),
			fen.Raw,
			fen,
			promotionChoices
		);

		return true;
	}

	internal static bool HasInsufficientMaterial(Fen fen)
	{
		var state = Parse(fen);
		var knightCount = 0;
		bool? bishopIsLightSquare = null;

		for (var squareIndex = 0; squareIndex < state.Board.Length; squareIndex++)
		{
			char piece = state.Board[squareIndex];
			if (piece == '\0' || char.ToLowerInvariant(piece) == 'k')
				continue;

			switch (char.ToLowerInvariant(piece))
			{
				case 'b':
					bool isLightSquare = IsLightSquare(squareIndex);
					if (bishopIsLightSquare.HasValue && bishopIsLightSquare.Value != isLightSquare)
						return false;

					bishopIsLightSquare = isLightSquare;
					break;
				case 'n':
					knightCount++;
					break;
				default:
					return false;
			}
		}

		return bishopIsLightSquare.HasValue
			? knightCount == 0
			: knightCount <= 1;
	}

	// This is a material test, not a complete solver for blocked dead positions.
	internal static bool CanPossiblyMate(Fen fen, char side)
	{
		if (side is not ('w' or 'b'))
			throw new ArgumentOutOfRangeException(nameof(side));

		var state = Parse(fen);
		var ownMinorCount = 0;
		var ownKnightCount = 0;
		var ownBishopCount = 0;
		var enemyCanBlockKnightMate = false;
		var hasPawnOrKnight = false;
		bool? bishopSquareColor = null;
		var bishopsOnBothColors = false;
		for (var square = 0; square < state.Board.Length; square++)
		{
			char piece = state.Board[square];
			if (piece == '\0')
				continue;
			char kind = char.ToLowerInvariant(piece);
			bool own = GetPieceColor(piece) == side;
			if (own && kind is 'p' or 'r' or 'q')
				return true;
			if (!own && kind is not ('k' or 'q'))
				enemyCanBlockKnightMate = true;
			if (kind is 'p' or 'n')
				hasPawnOrKnight = true;
			if (kind == 'n' && own)
			{
				ownKnightCount++;
				ownMinorCount++;
			}
			if (kind == 'b')
			{
				bool squareColor = IsLightSquare(square);
				if (bishopSquareColor.HasValue && bishopSquareColor != squareColor)
					bishopsOnBothColors = true;
				bishopSquareColor = squareColor;
				if (own)
				{
					ownBishopCount++;
					ownMinorCount++;
				}
			}
		}

		if (ownKnightCount > 0)
			return ownMinorCount > 1 || enemyCanBlockKnightMate;
		if (ownBishopCount > 0)
			return bishopsOnBothColors || hasPawnOrKnight;
		return false;
	}

	internal static string BuildRepetitionKey(Fen fen)
	{
		string enPassant = string.Empty;
		if (fen.EnPassantTarget != "-")
		{
			var state = Parse(fen);
			int targetIndex = ParseSquare(state.EnPassantTarget.AsSpan());
			int pawnRank = state.ActiveColor == 'w' ? 4 : 3;
			for (int fileDelta = -1; fileDelta <= 1; fileDelta += 2)
			{
				int file = GetFile(targetIndex) + fileDelta;
				if (!IsOnBoard(file, pawnRank))
					continue;
				int fromIndex = GetIndex(file, pawnRank);
				if (state.Board[fromIndex] == (state.ActiveColor == 'w' ? 'P' : 'p') &&
					IsEnPassantCapture(state, fromIndex, targetIndex) &&
					IsLegalMove(state, fromIndex, targetIndex, isEnPassant: true))
				{
					enPassant = state.EnPassantTarget;
					break;
				}
			}
		}

		return $"{fen.PiecePlacement} {fen.ActiveColor} {NormalizeCastlingRights(fen.CastlingRights)} {enPassant}";
	}

	internal static ImmutableDictionary<string, MoveClassification> ClassifyMoves(
		Fen                 fen,
		IEnumerable<string> legalMoves)
	{
		if (legalMoves is null) throw new ArgumentNullException(nameof(legalMoves));

		var builder = ImmutableDictionary.CreateBuilder<string, MoveClassification>(StringComparer.Ordinal);
		LocalPositionState? state = null;
		foreach (string move in legalMoves)
		{
			string normalizedMove = NormalizeMove(move);
			state ??= Parse(fen);
			builder[move] = ClassifyNormalized(state.Value, normalizedMove);
		}

		return builder.ToImmutable();
	}

	internal static ImmutableDictionary<string, MoveClassification> ClassifyMovesFully(
		Fen                 fen,
		IEnumerable<string> legalMoves) =>
		ClassifyMovesFully(fen, legalMoves, default);

	internal static ImmutableDictionary<string, MoveClassification> ClassifyMovesFully(
		Fen                 fen,
		IEnumerable<string> legalMoves,
		CancellationToken   ct)
	{
		if (legalMoves is null) throw new ArgumentNullException(nameof(legalMoves));

		var builder = ImmutableDictionary.CreateBuilder<string, MoveClassification>(StringComparer.Ordinal);
		LocalPositionState? state = null;
		foreach (string move in legalMoves)
		{
			ct.ThrowIfCancellationRequested();
			string normalizedMove = NormalizeMove(move);
			state ??= Parse(fen);
			var structural = ClassifyNormalized(state.Value, normalizedMove);
			builder[move] = ResolveTacticalOutcome(state.Value, normalizedMove, structural);
		}

		return builder.ToImmutable();
	}

	internal static MoveClassification ClassifyMove(Fen fen, string move)
	{
		string normalizedMove = NormalizeMove(move);
		return ClassifyNormalized(Parse(fen), normalizedMove);
	}

	internal static MoveClassification ClassifyMoveFully(Fen fen, string move)
	{
		string normalizedMove = NormalizeMove(move);
		var state = Parse(fen);
		var structural = ClassifyNormalized(state, normalizedMove);
		return ResolveTacticalOutcome(state, normalizedMove, structural);
	}

	private static MoveClassification ResolveTacticalOutcome(
		LocalPositionState state,
		string             move,
		MoveClassification structural)
	{
		if (!IsLegalCandidate(state, move))
			throw new InvalidOperationException("The move is not legal in the supplied FEN position.");

		var next = ApplyMove(state, move, structural);
		bool isCheck = IsKingInCheck(next.Board, next.ActiveColor);
		bool hasReply = HasAnyLegalMove(next);
		return structural.WithTacticalOutcome(
			isCheck,
			isCheck && !hasReply,
			!isCheck && !hasReply
		);
	}

	internal static LocalPositionState ApplyMove(
		LocalPositionState state,
		string             move,
		MoveClassification structural)
	{
		var board = (char[])state.Board.Clone();
		int fromIndex = ParseSquare(move.AsSpan(0, 2));
		int toIndex = ParseSquare(move.AsSpan(2, 2));
		char movingPiece = board[fromIndex];
		char targetPiece = board[toIndex];

		board[fromIndex] = '\0';

		if (structural.IsEnPassant)
		{
			int capturedPawnIndex = state.ActiveColor == 'w' ? toIndex - 8 : toIndex + 8;
			board[capturedPawnIndex] = '\0';
		}

		if (structural.IsKingsideCastling)
			MoveRookForCastling(board, toIndex, true);
		else if (structural.IsQueensideCastling)
			MoveRookForCastling(board, toIndex, false);

		board[toIndex] = structural.IsPromotion
			? ColorizePromotionPiece(move[4], state.ActiveColor)
			: movingPiece;

		string castlingRights = UpdateCastlingRights(
			state.CastlingRights,
			movingPiece,
			fromIndex,
			targetPiece,
			toIndex
		);

		string enPassantTarget = structural.IsDoublePawnPush
			? FormatSquare((fromIndex + toIndex) / 2)
			: string.Empty;

		int nextHalfmoveClock = char.ToLowerInvariant(movingPiece) == 'p' || structural.IsCapture
			? 0
			: checked(state.HalfmoveClock + 1);

		int nextFullmoveNumber = state.ActiveColor == 'b'
			? checked(state.FullmoveNumber + 1)
			: state.FullmoveNumber;

		return new(
			board,
			Opposite(state.ActiveColor),
			castlingRights,
			enPassantTarget,
			nextHalfmoveClock,
			nextFullmoveNumber
		);
	}

	private static MoveClassification ClassifyNormalized(LocalPositionState state, string move)
	{
		int fromIndex = ParseSquare(move.AsSpan(0, 2));
		int toIndex = ParseSquare(move.AsSpan(2, 2));
		char movingPiece = state.Board[fromIndex];
		if (movingPiece == '\0')
			throw new ArgumentException($"No piece exists on source square '{move[..2]}'.", nameof(move));

		char target = state.Board[toIndex];
		char? targetPiece = target == '\0' ? null : target;
		bool isPawn = char.ToLowerInvariant(movingPiece) == 'p';
		bool isPromotion = move.Length == 5;
		char? promotionPiece = isPromotion ? move[4] : null;
		bool isEnPassant = isPawn &&
			GetFile(fromIndex) != GetFile(toIndex) &&
			targetPiece is null &&
			state.EnPassantTarget.AsSpan().SequenceEqual(move.AsSpan(2, 2));

		char? capturedPiece = targetPiece;
		if (isEnPassant)
		{
			int capturedPawnIndex = state.ActiveColor == 'w' ? toIndex - 8 : toIndex + 8;
			char captured = state.Board[capturedPawnIndex];
			capturedPiece = captured == '\0' ? null : captured;
		}

		var flags = MoveClassificationFlags.None;
		if (isPromotion)
			flags |= MoveClassificationFlags.Promotion;

		if (capturedPiece.HasValue)
			flags |= MoveClassificationFlags.Capture;

		if (isEnPassant)
			flags |= MoveClassificationFlags.EnPassant;

		if (IsKingsideCastlingMove(movingPiece, fromIndex, toIndex))
			flags |= MoveClassificationFlags.KingsideCastling;
		else if (IsQueensideCastlingMove(movingPiece, fromIndex, toIndex))
			flags |= MoveClassificationFlags.QueensideCastling;

		if (IsDoublePawnPush(movingPiece, fromIndex, toIndex))
			flags |= MoveClassificationFlags.DoublePawnPush;

		if ((flags &
			 (MoveClassificationFlags.Capture |
			  MoveClassificationFlags.EnPassant |
			  MoveClassificationFlags.Promotion |
			  MoveClassificationFlags.KingsideCastling |
			  MoveClassificationFlags.QueensideCastling)) ==
			0)
			flags |= MoveClassificationFlags.Normal;

		return MoveClassification.CreateStructural(flags, movingPiece, capturedPiece, promotionPiece);
	}

	private static bool IsDoublePawnPush(char movingPiece, int fromIndex, int toIndex) =>
		char.ToLowerInvariant(movingPiece) == 'p' &&
		GetFile(fromIndex) == GetFile(toIndex) &&
		Math.Abs(GetRank(toIndex) - GetRank(fromIndex)) == 2;

	private static bool IsKingsideCastlingMove(char movingPiece, int fromIndex, int toIndex) =>
		(movingPiece == 'K' && fromIndex == 4 && toIndex == 6) ||
		(movingPiece == 'k' && fromIndex == 60 && toIndex == 62);

	private static bool IsQueensideCastlingMove(char movingPiece, int fromIndex, int toIndex) =>
		(movingPiece == 'K' && fromIndex == 4 && toIndex == 2) ||
		(movingPiece == 'k' && fromIndex == 60 && toIndex == 58);

	private static string UpdateCastlingRights(
		string castlingRights,
		char   movingPiece,
		int    fromIndex,
		char   capturedPiece,
		int    toIndex)
	{
		string updated = castlingRights;
		switch (movingPiece)
		{
			case 'K':
				updated = RemoveCastlingRights(updated, "KQ");
				break;
			case 'k':
				updated = RemoveCastlingRights(updated, "kq");
				break;
			case 'R':
				if (fromIndex == GetIndex(0, 0))
					updated = RemoveCastlingRights(updated, "Q");
				else if (fromIndex == GetIndex(7, 0))
					updated = RemoveCastlingRights(updated, "K");
				break;
			case 'r':
				if (fromIndex == GetIndex(0, 7))
					updated = RemoveCastlingRights(updated, "q");
				else if (fromIndex == GetIndex(7, 7))
					updated = RemoveCastlingRights(updated, "k");
				break;
		}

		switch (capturedPiece)
		{
			case 'R':
				if (toIndex == GetIndex(0, 0))
					updated = RemoveCastlingRights(updated, "Q");
				else if (toIndex == GetIndex(7, 0))
					updated = RemoveCastlingRights(updated, "K");
				break;
			case 'r':
				if (toIndex == GetIndex(0, 7))
					updated = RemoveCastlingRights(updated, "q");
				else if (toIndex == GetIndex(7, 7))
					updated = RemoveCastlingRights(updated, "k");
				break;
		}

		return updated;
	}

	private static string RemoveCastlingRights(string castlingRights, string rightsToRemove)
	{
		if (string.IsNullOrEmpty(castlingRights))
			return string.Empty;

		string updated = castlingRights;
		foreach (char right in rightsToRemove)
			updated = updated.Replace(right.ToString(), string.Empty, StringComparison.Ordinal);

		return updated;
	}

	private static void MoveRookForCastling(Span<char> board, int kingDestinationIndex, bool kingside)
	{
		int rank = GetRank(kingDestinationIndex);
		int rookFromIndex = kingside ? GetIndex(7, rank) : GetIndex(0, rank);
		int rookToIndex = kingside ? GetIndex(5, rank) : GetIndex(3, rank);
		board[rookToIndex] = board[rookFromIndex];
		board[rookFromIndex] = '\0';
	}

	internal static char ColorizePromotionPiece(char promotionPiece, char color)
	{
		char normalizedPiece = char.ToLowerInvariant(promotionPiece);
		return color == 'w' ? char.ToUpperInvariant(normalizedPiece) : normalizedPiece;
	}

	internal static char Opposite(char color) => color == 'w' ? 'b' : 'w';

	private static bool IsLightSquare(int index) => ((GetFile(index) + GetRank(index)) & 1) == 0;
}

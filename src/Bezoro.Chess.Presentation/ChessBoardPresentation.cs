using Bezoro.Chess.Presentation.Internal;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.Presentation;

/// <summary>
/// Immutable renderer-independent board data. Build after a match or selection change, then share the same value
/// with 2D and 3D renderers. Creation and square queries allocate no managed objects for valid match snapshots.
/// The projection does not search legal moves, start an engine, advance clocks or retain the match owner.
/// </summary>
public readonly struct ChessBoardPresentation
{
    private readonly BoardPieces _pieces;
    private readonly ulong _legalMoves, _legalCaptures;
    private readonly ChessSquare? _promotionSquare, _checkedKing;
    private readonly bool _isCheckmate;

    private ChessBoardPresentation(in BoardPieces pieces, string positionKey, ChessSquare? selectedSquare,
        ulong legalMoves, ulong legalCaptures, ChessMoveVisual? lastMove, ChessSquare? promotionSquare,
        ChessSquare? checkedKing, bool isCheckmate)
    {
        _pieces = pieces;
        PositionKey = positionKey;
        SelectedSquare = selectedSquare;
        _legalMoves = legalMoves;
        _legalCaptures = legalCaptures;
        LastMove = lastMove;
        _promotionSquare = promotionSquare;
        _checkedKing = checkedKing;
        _isCheckmate = isCheckmate;
    }

    /// <summary>Stable source position key; compare it when deciding whether to animate a newly played move.</summary>
    public string PositionKey { get; }
    /// <summary>Currently selected friendly piece, or no value when the selection is unavailable.</summary>
    public ChessSquare? SelectedSquare { get; }
    /// <summary>Last move remaining in the supplied history. Undo automatically restores the preceding move.</summary>
    public ChessMoveVisual? LastMove { get; }

    /// <summary>
    /// Projects a valid match snapshot. Selection is accepted only for an active friendly piece while play is ongoing;
    /// a pending promotion instead selects its source. Legal destinations come from the snapshot's existing legal moves.
    /// </summary>
    public static ChessBoardPresentation FromMatch(in PlayableMatchState state, ChessSquare? selectedSquare = null)
    {
        if (state.Fen.PiecePlacement is null)
            throw new ArgumentException("A match with a valid current FEN is required.", nameof(state));

        BoardPieces pieces = BoardPieces.Parse(state.Fen.PiecePlacement.AsSpan());
        ulong legalMoves = 0, legalCaptures = 0;
        ChessSquare? promotionSquare = null;
        if (state.PendingPromotion is { } pending)
        {
            selectedSquare = ParseSquare(pending.From.AsSpan());
            promotionSquare = ParseSquare(pending.To.AsSpan());
        }
        else if (state.Result.IsTerminal || !selectedSquare.HasValue ||
                 !BelongsTo(pieces.GetSymbol(selectedSquare.Value), state.Fen.ActiveColor))
        {
            selectedSquare = null;
        }
        else if (!state.LegalMoves.IsDefault)
        {
            foreach (string notation in state.LegalMoves)
            {
                ReadOnlySpan<char> move = notation.AsSpan();
                ValidateMove(move);
                ChessSquare from = ParseSquare(move.Slice(0, 2));
                if (from != selectedSquare.Value) continue;
                ChessSquare to = ParseSquare(move.Slice(2, 2));
                char piece = pieces.GetSymbol(from);
                bool isCapture = pieces.GetSymbol(to) != '\0' ||
                    char.ToLowerInvariant(piece) == 'p' && from.FileIndex != to.FileIndex;
                if (isCapture) legalCaptures |= BoardPieces.Bit(to);
                else legalMoves |= BoardPieces.Bit(to);
            }
        }

        ChessMoveVisual? lastMove = null;
        if (!state.MoveHistory.IsDefaultOrEmpty)
            lastMove = ReadLastMove(state.MoveHistory[state.MoveHistory.Length - 1]);

        ChessSquare? checkedKing = state.Fen.IsInCheck() ? pieces.GetKing(state.Fen.ActiveColor) : null;
        bool isMate = state.Result.Reason == PlayableMatchResultReason.Checkmate;
        return new ChessBoardPresentation(in pieces, state.PositionKey, selectedSquare, legalMoves, legalCaptures,
            lastMove, promotionSquare, checkedKing, isMate);
    }

    /// <summary>Returns the FEN piece symbol on a square, or the null character for an empty square.</summary>
    public char GetPieceSymbol(ChessSquare square) => _pieces.GetSymbol(square);

    /// <summary>Returns every annotation on a square, including overlaps.</summary>
    public ChessSquareHighlights GetHighlights(ChessSquare square)
    {
        var result = ChessSquareHighlights.None;
        ulong bit = BoardPieces.Bit(square);
        if (SelectedSquare == square) result |= ChessSquareHighlights.Selected;
        if ((_legalMoves & bit) != 0) result |= ChessSquareHighlights.LegalMove;
        if ((_legalCaptures & bit) != 0) result |= ChessSquareHighlights.LegalCapture;
        if (_promotionSquare == square) result |= ChessSquareHighlights.Promotion;
        if (_checkedKing == square)
            result |= _isCheckmate ? ChessSquareHighlights.Checkmate : ChessSquareHighlights.Check;
        if (LastMove is { } move)
        {
            if (move.From == square || move.To == square) result |= ChessSquareHighlights.LastMove;
            if (move.CapturedSquare == square) result |= ChessSquareHighlights.Capture;
            if (move.CastlingRookFrom == square || move.CastlingRookTo == square)
                result |= ChessSquareHighlights.CastlingRook;
            if (move.PromotionPiece.HasValue && move.To == square) result |= ChessSquareHighlights.Promotion;
        }
        return result;
    }

    private static ChessMoveVisual ReadLastMove(in PlayedMove playedMove)
    {
        ReadOnlySpan<char> move = playedMove.Move.AsSpan();
        ValidateMove(move);
        ChessSquare from = ParseSquare(move.Slice(0, 2));
        ChessSquare to = ParseSquare(move.Slice(2, 2));
        ReadOnlySpan<char> parentFen = playedMove.ParentPositionKey.AsSpan();
        int placementEnd = parentFen.IndexOfAny(' ', '\t');
        BoardPieces previous = BoardPieces.Parse(placementEnd < 0 ? parentFen : parentFen.Slice(0, placementEnd));
        char piece = previous.GetSymbol(from);
        char captured = previous.GetSymbol(to);
        ChessSquare? capturedSquare = captured == '\0' ? null : to;
        if (char.ToLowerInvariant(piece) == 'p' && from.FileIndex != to.FileIndex && captured == '\0')
        {
            capturedSquare = new ChessSquare(to.FileIndex, from.RankIndex);
            captured = previous.GetSymbol(capturedSquare.Value);
        }
        ChessSquare? rookFrom = null, rookTo = null;
        if (char.ToLowerInvariant(piece) == 'k' && Math.Abs(from.FileIndex - to.FileIndex) == 2)
        {
            bool kingSide = to.FileIndex > from.FileIndex;
            rookFrom = new ChessSquare(kingSide ? 7 : 0, from.RankIndex);
            rookTo = new ChessSquare(kingSide ? 5 : 3, from.RankIndex);
        }
        char? promotion = move.Length == 5
            ? char.IsUpper(piece) ? char.ToUpperInvariant(move[4]) : move[4]
            : null;
        return new ChessMoveVisual(from, to, piece, capturedSquare, captured == '\0' ? null : captured,
            rookFrom, rookTo, promotion);
    }

    private static bool BelongsTo(char piece, char side) => piece != '\0' &&
        (side == 'w' ? piece is >= 'A' and <= 'Z' : piece is >= 'a' and <= 'z');

    private static ChessSquare ParseSquare(ReadOnlySpan<char> square)
    {
        if (square.Length != 2 || !ChessSquare.TryCreate(square[0] - 'a', square[1] - '1', out var result))
            throw new ArgumentException("A match snapshot must contain lowercase algebraic squares.");
        return result;
    }

    private static void ValidateMove(ReadOnlySpan<char> move)
    {
        if (move.Length is not (4 or 5))
            throw new ArgumentException("A match snapshot must contain UCI coordinate moves.");
    }
}

using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.Presentation;

/// <summary>Piece motion and special-move geometry for highlighting or animating a completed move.</summary>
public readonly struct ChessMoveVisual
{
    internal ChessMoveVisual(ChessSquare from, ChessSquare to, char movingPiece,
        ChessSquare? capturedSquare, char? capturedPiece, ChessSquare? rookFrom, ChessSquare? rookTo,
        char? promotionPiece)
    {
        From = from;
        To = to;
        MovingPiece = movingPiece;
        CapturedSquare = capturedSquare;
        CapturedPiece = capturedPiece;
        CastlingRookFrom = rookFrom;
        CastlingRookTo = rookTo;
        PromotionPiece = promotionPiece;
    }

    /// <summary>Primary piece source.</summary>
    public ChessSquare From { get; }
    /// <summary>Primary piece destination.</summary>
    public ChessSquare To { get; }
    /// <summary>Original FEN symbol, preserving the moving piece's color.</summary>
    public char MovingPiece { get; }
    /// <summary>Actual victim square before capture; differs from the destination for en passant.</summary>
    public ChessSquare? CapturedSquare { get; }
    /// <summary>Captured piece's original FEN symbol.</summary>
    public char? CapturedPiece { get; }
    /// <summary>Rook source during castling.</summary>
    public ChessSquare? CastlingRookFrom { get; }
    /// <summary>Rook destination during castling.</summary>
    public ChessSquare? CastlingRookTo { get; }
    /// <summary>Promoted FEN symbol preserving color, or no value for a nonpromotion.</summary>
    public char? PromotionPiece { get; }
}

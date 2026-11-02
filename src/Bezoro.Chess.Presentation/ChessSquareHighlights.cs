namespace Bezoro.Chess.Presentation;

/// <summary>Independent visual annotations that may overlap on a board square.</summary>
[Flags]
public enum ChessSquareHighlights
{
    /// <summary>No annotation.</summary>
    None = 0,
    /// <summary>The currently selected friendly piece.</summary>
    Selected = 1,
    /// <summary>A legal quiet destination for the selected piece.</summary>
    LegalMove = 2,
    /// <summary>A legal capturing destination, including en passant.</summary>
    LegalCapture = 4,
    /// <summary>The source or destination of the last played move.</summary>
    LastMove = 8,
    /// <summary>The square occupied by the last captured piece before its capture.</summary>
    Capture = 16,
    /// <summary>The source or destination of the rook during the last castle.</summary>
    CastlingRook = 32,
    /// <summary>A promotion destination, pending or completed by the last move.</summary>
    Promotion = 64,
    /// <summary>The king belonging to the side currently in check.</summary>
    Check = 128,
    /// <summary>The king belonging to the checkmated side.</summary>
    Checkmate = 256
}

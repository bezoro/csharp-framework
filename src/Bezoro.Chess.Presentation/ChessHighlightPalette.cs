using Bezoro.Core.Types;

namespace Bezoro.Chess.Presentation;

/// <summary>Immutable configurable RGBA overlay colors shared by 2D and 3D renderers.</summary>
public sealed class ChessHighlightPalette
{
    /// <summary>The default palette. Colors include opacity for use as overlays.</summary>
    public static ChessHighlightPalette Default { get; } = new();

    /// <summary>Creates a palette; omitted colors use the defaults.</summary>
    public ChessHighlightPalette(
        Color? selected = null, Color? legalMove = null, Color? legalCapture = null,
        Color? lastMove = null, Color? capture = null, Color? castlingRook = null,
        Color? promotion = null, Color? check = null, Color? checkmate = null)
    {
        Selected = selected ?? new Color(255, 215, 0, 150);
        LegalMove = legalMove ?? new Color(80, 180, 100, 110);
        LegalCapture = legalCapture ?? new Color(240, 130, 60, 150);
        LastMove = lastMove ?? new Color(190, 210, 60, 110);
        Capture = capture ?? new Color(230, 100, 70, 130);
        CastlingRook = castlingRook ?? new Color(100, 160, 230, 110);
        Promotion = promotion ?? new Color(175, 100, 235, 160);
        Check = check ?? new Color(240, 70, 60, 170);
        Checkmate = checkmate ?? new Color(180, 20, 30, 210);
    }

    /// <summary>Selected-piece overlay.</summary>
    public Color Selected { get; }
    /// <summary>Quiet legal-destination overlay.</summary>
    public Color LegalMove { get; }
    /// <summary>Capturing legal-destination overlay.</summary>
    public Color LegalCapture { get; }
    /// <summary>Last primary move overlay.</summary>
    public Color LastMove { get; }
    /// <summary>Last capture victim overlay.</summary>
    public Color Capture { get; }
    /// <summary>Last castling rook motion overlay.</summary>
    public Color CastlingRook { get; }
    /// <summary>Pending or completed promotion overlay.</summary>
    public Color Promotion { get; }
    /// <summary>Checked-king overlay.</summary>
    public Color Check { get; }
    /// <summary>Checkmated-king overlay.</summary>
    public Color Checkmate { get; }

    /// <summary>
    /// Resolves a single overlay using this priority: mate, check, promotion, selection, legal capture,
    /// legal quiet move, capture, castling rook, last move. Renderers may instead combine the individual colors.
    /// </summary>
    public Color GetOverlay(ChessSquareHighlights highlights)
    {
        if ((highlights & ChessSquareHighlights.Checkmate) != 0) return Checkmate;
        if ((highlights & ChessSquareHighlights.Check) != 0) return Check;
        if ((highlights & ChessSquareHighlights.Promotion) != 0) return Promotion;
        if ((highlights & ChessSquareHighlights.Selected) != 0) return Selected;
        if ((highlights & ChessSquareHighlights.LegalCapture) != 0) return LegalCapture;
        if ((highlights & ChessSquareHighlights.LegalMove) != 0) return LegalMove;
        if ((highlights & ChessSquareHighlights.Capture) != 0) return Capture;
        if ((highlights & ChessSquareHighlights.CastlingRook) != 0) return CastlingRook;
        if ((highlights & ChessSquareHighlights.LastMove) != 0) return LastMove;
        return Color.Transparent;
    }
}

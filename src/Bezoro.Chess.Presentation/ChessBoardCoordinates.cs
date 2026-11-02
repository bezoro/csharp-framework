using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.Presentation;

/// <summary>Which player's pieces appear nearest the viewer.</summary>
public enum ChessBoardOrientation
{
    /// <summary>White is nearest; the bottom-left cell is a1.</summary>
    White,
    /// <summary>Black is nearest; the bottom-left cell is h8.</summary>
    Black
}

/// <summary>Maps board squares to an engine-independent display grid with its origin at bottom-left.</summary>
public static class ChessBoardCoordinates
{
    /// <summary>Converts a display cell to an algebraic board square. Both indexes range from zero to seven.</summary>
    public static ChessSquare FromDisplayCell(int column, int row, ChessBoardOrientation orientation)
    {
        Validate(orientation);
        var cell = new ChessSquare(column, row);
        return orientation == ChessBoardOrientation.White
            ? cell
            : new ChessSquare(7 - column, 7 - row);
    }

    /// <summary>Gets the display column for a square.</summary>
    public static int GetColumn(ChessSquare square, ChessBoardOrientation orientation)
    {
        Validate(orientation);
        return orientation == ChessBoardOrientation.White ? square.FileIndex : 7 - square.FileIndex;
    }

    /// <summary>Gets the display row for a square, increasing upwards from the bottom edge.</summary>
    public static int GetRow(ChessSquare square, ChessBoardOrientation orientation)
    {
        Validate(orientation);
        return orientation == ChessBoardOrientation.White ? square.RankIndex : 7 - square.RankIndex;
    }

    private static void Validate(ChessBoardOrientation orientation)
    {
        if (orientation is not (ChessBoardOrientation.White or ChessBoardOrientation.Black))
            throw new ArgumentOutOfRangeException(nameof(orientation));
    }
}

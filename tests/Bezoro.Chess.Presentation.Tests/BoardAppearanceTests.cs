using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.Core.Types;
using Xunit;

namespace Bezoro.Chess.Presentation.Tests;

public sealed class BoardAppearanceTests
{
    [Fact]
    public void Orientation_RoundTripsEveryCellAndKeepsAlgebraicCoordinatesStable()
    {
        foreach (var orientation in new[] { ChessBoardOrientation.White, ChessBoardOrientation.Black })
        for (var row = 0; row < 8; row++)
        for (var column = 0; column < 8; column++)
        {
            var square = ChessBoardCoordinates.FromDisplayCell(column, row, orientation);
            Assert.Equal(column, ChessBoardCoordinates.GetColumn(square, orientation));
            Assert.Equal(row, ChessBoardCoordinates.GetRow(square, orientation));
        }
        Assert.Equal(new ChessSquare(0, 0), ChessBoardCoordinates.FromDisplayCell(0, 0, ChessBoardOrientation.White));
        Assert.Equal(new ChessSquare(7, 7), ChessBoardCoordinates.FromDisplayCell(0, 0, ChessBoardOrientation.Black));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChessBoardCoordinates.FromDisplayCell(8, 0, ChessBoardOrientation.White));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChessBoardCoordinates.GetRow(default, (ChessBoardOrientation)2));
    }

    [Fact]
    public void Palette_UsesConfiguredColorsAndExplicitPriority()
    {
        var red = new Color(250, 0, 0, 255);
        var blue = new Color(0, 0, 250, 255);
        var palette = new ChessHighlightPalette(selected: blue, checkmate: red);
        Assert.Equal(blue, palette.GetOverlay(ChessSquareHighlights.Selected | ChessSquareHighlights.LastMove));
        Assert.Equal(red, palette.GetOverlay(ChessSquareHighlights.Checkmate | ChessSquareHighlights.Selected));
        Assert.Equal(Color.Transparent, palette.GetOverlay(ChessSquareHighlights.None));
        Assert.Equal(palette.Promotion, palette.GetOverlay(ChessSquareHighlights.Promotion | ChessSquareHighlights.Capture));
        Assert.Equal(palette.LegalCapture, palette.GetOverlay(ChessSquareHighlights.LegalCapture | ChessSquareHighlights.LastMove));
    }

    [Fact]
    public void Skin_ResolvesBothRenderersWithoutOwningAssets()
    {
        var skin = new ChessPieceSkin("marble", "sprites/marble", "models/marble");
        Assert.Equal("marble", skin.Id);
        Assert.Equal("sprites/marble", skin.GetAssetKey(ChessBoardVisualMode.TwoDimensional));
        Assert.Equal("models/marble", skin.GetAssetKey(ChessBoardVisualMode.ThreeDimensional));
        Assert.Throws<ArgumentException>(() => new ChessPieceSkin(" ", "2d", "3d"));
        Assert.Throws<ArgumentException>(() => new ChessPieceSkin("marble", "2d", null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => skin.GetAssetKey((ChessBoardVisualMode)2));
        Assert.Throws<InvalidOperationException>(() => default(ChessPieceSkin).GetAssetKey(ChessBoardVisualMode.TwoDimensional));
    }
}

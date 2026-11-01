using Bezoro.Chess.UCI.API.Common.Enums;
using Bezoro.Chess.UCI.API.Types;
using FluentAssertions;

namespace Bezoro.Chess.UCI.Tests.API.Types;

public sealed class ParsedMoveTests
{
    [Theory]
    [InlineData("b7b8b", "b7", "b8", PieceColor.White)]
    [InlineData("b2b1b", "b2", "b1", PieceColor.Black)]
    public void BishopPromotionFromBFile_PreservesSquaresSuffixAndPieceColor(
        string notation, string from, string to, PieceColor color)
    {
        var parsed = ParsedMove.FromNotation(notation);
        parsed.From.Should().Be(from);
        parsed.To.Should().Be(to);
        parsed.Notation.Should().Be(notation);
        parsed.PromotionPiece!.Value.Type.Should().Be(PieceType.Bishop);
        parsed.PromotionPiece.Value.Color.Should().Be(color);
    }

    [Fact]
    public void ExplicitPieceAndPromotion_NormalizesCoordinatesAndRetainsRawText()
    {
        var parsed = ParsedMove.FromNotation(" Pb7B8Q ");
        parsed.Notation.Should().Be("b7b8q");
        parsed.Raw.Should().Be(" Pb7B8Q ");
        parsed.MovingPiece.Type.Should().Be(PieceType.Pawn);
        parsed.PromotionPiece!.Value.Color.Should().Be(PieceColor.White);
    }

    [Theory]
    [InlineData("e9e4")]
    [InlineData("e2e4x")]
    [InlineData("e2e4q")]
    [InlineData("e2e2")]
    public void MalformedCoordinate_IsRejected(string notation)
    {
        Action parse = () => ParsedMove.FromNotation(notation);
        parse.Should().Throw<ArgumentException>();
    }
}

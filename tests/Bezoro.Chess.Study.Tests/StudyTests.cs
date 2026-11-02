using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Xunit;

namespace Bezoro.Chess.Study.Tests;

public sealed class StudyTests
{
    [Fact]
    public void UndoRetainsOriginalLineAndCreatesNavigableVariation()
    {
        var study = new ChessStudy();
        StudyNode e4 = study.PlaySan("e4");
        StudyNode e5 = study.PlaySan("e5");
        study.PlaySan("Nf3");
        Assert.True(study.Undo());
        Assert.True(study.Undo());
        Assert.Same(e4, study.Current);
        StudyNode c5 = study.PlaySan("c5");
        Assert.Equal(2, e4.VariationCount);
        Assert.Same(e5, e4.GetVariation(0));
        Assert.Same(c5, e4.GetVariation(1));
        study.GoTo(e4);
        Assert.True(study.Redo());
        Assert.Same(e5, study.Current);
        Assert.Equal(new[] { "e2e4", "e7e5" }, study.GetCurrentMoves());
        study.GoTo(study.Root);
        Assert.Same(e4, study.Play("e2e4"));
        Assert.Equal(5, study.NodeCount);
        Assert.Throws<ArgumentException>(() => study.GoTo(new ChessStudy().Root));
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", "e2e4", "e4")]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R w KQkq - 0 1", "e1g1", "O-O")]
    [InlineData("r3k2r/8/8/8/8/8/8/R3K2R b KQkq - 0 1", "e8c8", "O-O-O")]
    [InlineData("4k3/8/8/3pP3/8/8/8/4K3 w - d6 0 1", "e5d6", "exd6")]
    [InlineData("4k3/P7/8/8/8/8/8/4K3 w - - 0 1", "a7a8q", "a8=Q+")]
    [InlineData("4k3/8/8/8/8/8/8/1N2KN2 w - - 0 1", "b1d2", "Nbd2")]
    [InlineData("4k3/8/8/8/8/R7/8/R3K3 w - - 0 1", "a1a2", "R1a2")]
    [InlineData("7k/8/5KQ1/8/8/8/8/8 w - - 0 1", "g6g7", "Qg7#")]
    [InlineData("8/7k/8/8/8/Q7/8/Q1Q4K w - - 0 1", "a1c3", "Qa1c3")]
    [InlineData("k4r2/8/8/8/8/5N2/8/1N3K2 w - - 0 1", "b1d2", "Nd2")]
    public void SanCoversSpecialMovesAndDisambiguation(string rawFen, string move, string san)
    {
        Fen position = Fen.Parse(rawFen)!.Value;
        Assert.Equal(san, ChessNotation.ToSan(position, move));
        Assert.Equal(move, ChessNotation.ParseSan(position, san));
        if (san.EndsWith("+") || san.EndsWith("#")) Assert.Equal(move, ChessNotation.ParseSan(position, san[..^1]));
    }

    [Fact]
    public void InvalidSanDoesNotMutateStudy()
    {
        var study = new ChessStudy(maxNodes: 2);
        Assert.Throws<FormatException>(() => study.PlaySan("e4+"));
        Assert.Throws<FormatException>(() => study.PlaySan("e5"));
        Assert.Throws<ArgumentException>(() => study.Play("a1a8"));
        Assert.Same(study.Root, study.Current);
        Assert.Equal(1, study.NodeCount);
        study.PlaySan("e4");
        Assert.Throws<InvalidOperationException>(() => study.PlaySan("e5"));
        Assert.True(study.Undo());
        Assert.False(study.Undo());
        Assert.True(study.Redo());
        Assert.False(study.Redo());
        Assert.Equal(2, study.NodeCount);
    }

    [Fact]
    public void RoundTripPreservesNestedVariationsAnnotationsTagsAndCursor()
    {
        const string input = "[White \"A \\\"quoted\\\" name\"]\n[Result \"*\"]\n{Opening} 1.e4! {centre} (1.d4 d5 (1...Nf6 $5) 2.c4) 1...e5 (1...c5 2.Nf3) 2.Nf3 *";
        ChessStudy study = Pgn.Parse(input);
        Assert.Same(study.Root, study.Current);
        Assert.Equal(10, study.NodeCount);
        Assert.Equal("Opening", study.Root.Comment);
        StudyNode e4 = study.Root.GetVariation(0);
        Assert.Equal("centre", e4.Comment);
        Assert.Equal((byte)1, e4.GetAnnotation(0));
        Assert.Equal("A \"quoted\" name", study.Tags["White"]);
        study.GoTo(e4);
        string output = Pgn.Write(study);
        Assert.Same(e4, study.Current);
        ChessStudy restored = Pgn.Parse(output);
        Assert.Equal(output, Pgn.Write(restored));
        Assert.Equal(study.NodeCount, restored.NodeCount);
    }

    [Fact]
    public void CustomBlackStartingPositionPreservesCountersAndSetup()
    {
        var study = new ChessStudy(Fen.Parse("4k3/8/8/8/8/8/8/4K2R b - - 15 42")!.Value);
        study.PlaySan("Kd7");
        string pgn = Pgn.Write(study);
        Assert.Contains("[SetUp \"1\"]", pgn);
        Assert.Contains("42... Kd7", pgn);
        ChessStudy restored = Pgn.Parse(pgn);
        Assert.Equal(study.Root.Position, restored.Root.Position);
        Assert.Equal(study.Current.Position, restored.Root.GetVariation(0).Position);
    }

    [Theory]
    [InlineData("1.e4")]
    [InlineData("1.e4 (1.d4 *")]
    [InlineData("1.e4 () *")]
    [InlineData("(1.e4) *")]
    [InlineData("1.e4 e5 ) *")]
    [InlineData("1.e4 $256 *")]
    [InlineData("$1 *")]
    [InlineData("1.e4 {unclosed *")]
    [InlineData("1.e4 {nested {comment}} *")]
    [InlineData("1.e4 e5 * 1.d4")]
    [InlineData("1..e4 *")]
    [InlineData("[Result \"1-0\"] 1.e4 0-1")]
    [InlineData("[FEN \"bad\"] *")]
    [InlineData("[SetUp \"1\"] *")]
    [InlineData("[White \"A\"] [White \"B\"] *")]
    [InlineData("[White \"bad\\x\"] *")]
    public void MalformedPgnIsRejected(string text) => Assert.Throws<FormatException>(() => Pgn.Parse(text));

    [Fact]
    public void ImportBudgetsBoundUntrustedWork()
    {
        Assert.Throws<FormatException>(() => Pgn.Parse("1.e4 *", new PgnReadLimits(maxInputCharacters: 2)));
        Assert.Throws<FormatException>(() => Pgn.Parse("1.e4 e5 *", new PgnReadLimits(maxNodes: 2)));
        Assert.Throws<FormatException>(() => Pgn.Parse("1.e4 (1.d4) *", new PgnReadLimits(maxVariationDepth: 0)));
        Assert.Throws<FormatException>(() => Pgn.Parse("{abcd} *", new PgnReadLimits(maxTokenCharacters: 3)));
        Assert.Throws<FormatException>(() => Pgn.Parse("[White \"abcd\"] *", new PgnReadLimits(maxTokenCharacters: 3)));
        Assert.Throws<FormatException>(() => Pgn.Parse("{ab} {cd} *", new PgnReadLimits(maxTokenCharacters: 3)));
    }

    [Fact]
    public void RepeatedVariationPathsKeepTheirOwnCommentsAndNeverReplaceTheMainLine()
    {
        var study = Pgn.Parse("1.e4 {main} (1.e4 {alternative} e5) c5 *");
        Assert.Equal(2, study.Root.VariationCount);
        var main = study.Root.GetVariation(0);
        var alternative = study.Root.GetVariation(1);
        Assert.Equal("main", main.Comment);
        Assert.Equal("alternative", alternative.Comment);
        Assert.Equal("c7c5", main.GetVariation(0).Move);
        Assert.Equal("e7e5", alternative.GetVariation(0).Move);
        string exported = Pgn.Write(study);
        Assert.Equal(exported, Pgn.Write(Pgn.Parse(exported)));
    }

    [Fact]
    public void SemicolonBracesAndVariationIntroductionsRetainTheirScope()
    {
        var study = Pgn.Parse("1.e4 ; note {idea}\n ({Alternative} 1.d4 {postmove}) *");
        Assert.Equal(string.Empty, study.Root.Comment);
        Assert.Equal(" note {idea}", study.Root.GetVariation(0).Comment);
        Assert.Equal("Alternative", study.Root.GetVariation(1).LeadingComment);
        Assert.Equal("postmove", study.Root.GetVariation(1).Comment);
        Assert.Throws<ArgumentException>(() => study.SetLeadingComment(study.Root.GetVariation(0), "unscoped"));
        string exported = Pgn.Write(study);
        Assert.Equal(exported, Pgn.Write(Pgn.Parse(exported)));
    }

    [Theory]
    [InlineData("[SetUp \"1\"] [FEN \"8/8/8/8/8/8/8/8 w - - 0 1\"] *")]
    [InlineData("[Variant \"Chess960\"] *")]
    public void InvalidPlayableSetupsAndUnsupportedVariantsHaveTheImportExceptionContract(string input) =>
        Assert.Throws<FormatException>(() => Pgn.Parse(input));

    [Fact]
    public void DeterministicGamesRoundTripAllLegalMovesWithoutAnEngine()
    {
        var random = new Random(48571);
        for (int game = 0; game < 12; game++)
        {
            var study = new ChessStudy();
            for (int ply = 0; ply < 48; ply++)
            {
                var legal = study.Current.Position.GetLegalMoves();
                if (legal.IsEmpty) break;
                string move = legal[random.Next(legal.Length)];
                Assert.Equal(move, ChessNotation.ParseSan(study.Current.Position, ChessNotation.ToSan(study.Current.Position, move)));
                study.Play(move);
            }
            string pgn = Pgn.Write(study);
            Assert.Equal(pgn, Pgn.Write(Pgn.Parse(pgn)));
        }
    }
}

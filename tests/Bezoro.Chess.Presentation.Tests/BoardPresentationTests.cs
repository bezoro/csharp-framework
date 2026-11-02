using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Xunit;

namespace Bezoro.Chess.Presentation.Tests;

public sealed class BoardPresentationTests
{
    [Fact]
    public void StartingPosition_HasExactPieceSymbolsAndSelectedLegalDestinations()
    {
        using var match = new UciPlayableMatchSession();
        var state = match.Refresh();
        var board = ChessBoardPresentation.FromMatch(in state, Square("e2"));
        const string rankOne = "RNBQKBNR";
        const string rankEight = "rnbqkbnr";
        for (var file = 0; file < 8; file++)
        {
            Assert.Equal(rankOne[file], board.GetPieceSymbol(new ChessSquare(file, 0)));
            Assert.Equal('P', board.GetPieceSymbol(new ChessSquare(file, 1)));
            Assert.Equal('p', board.GetPieceSymbol(new ChessSquare(file, 6)));
            Assert.Equal(rankEight[file], board.GetPieceSymbol(new ChessSquare(file, 7)));
            for (var rank = 2; rank < 6; rank++)
                Assert.Equal('\0', board.GetPieceSymbol(new ChessSquare(file, rank)));
        }
        Assert.Equal(ChessSquareHighlights.Selected, board.GetHighlights(Square("e2")));
        Assert.Equal(ChessSquareHighlights.LegalMove, board.GetHighlights(Square("e3")));
        Assert.Equal(ChessSquareHighlights.LegalMove, board.GetHighlights(Square("e4")));
        Assert.Equal(ChessSquareHighlights.None, board.GetHighlights(Square("e5")));
        Assert.Null(board.LastMove);
    }

    [Fact]
    public void OpposingOrEmptySelection_DoesNotAdvertiseMoves()
    {
        using var match = new UciPlayableMatchSession();
        var state = match.Refresh();
        Assert.Null(ChessBoardPresentation.FromMatch(in state, Square("e7")).SelectedSquare);
        Assert.Null(ChessBoardPresentation.FromMatch(in state, Square("e4")).SelectedSquare);
    }

    [Fact]
    public void LegalCaptureAndLastCapture_HaveDistinctAnnotations()
    {
        using var match = new UciPlayableMatchSession();
        Play(match, "e2e4", "d7d5");
        var state = match.Refresh();
        var before = ChessBoardPresentation.FromMatch(in state, Square("e4"));
        Assert.True((before.GetHighlights(Square("d5")) & ChessSquareHighlights.LegalCapture) != 0);
        match.ApplyMove("e4d5");
        state = match.Refresh();
        var after = ChessBoardPresentation.FromMatch(in state);
        Assert.Equal('P', after.GetPieceSymbol(Square("d5")));
        Assert.Equal(Square("d5"), after.LastMove!.Value.CapturedSquare);
        Assert.Equal('p', after.LastMove.Value.CapturedPiece);
        Assert.Equal(ChessSquareHighlights.LastMove | ChessSquareHighlights.Capture,
            after.GetHighlights(Square("d5")));
    }

    [Fact]
    public async Task EnPassant_HighlightsDestinationAndActualVictimSquare()
    {
        using var match = new UciPlayableMatchSession();
        await Load(match, "7k/8/8/3pP3/8/8/8/7K w - d6 0 1");
        var state = match.Refresh();
        var before = ChessBoardPresentation.FromMatch(in state, Square("e5"));
        Assert.Equal(ChessSquareHighlights.LegalCapture, before.GetHighlights(Square("d6")));
        match.ApplyMove("e5d6");
        state = match.Refresh();
        var after = ChessBoardPresentation.FromMatch(in state);
        Assert.Equal(Square("d5"), after.LastMove!.Value.CapturedSquare);
        Assert.Equal('p', after.LastMove.Value.CapturedPiece);
        Assert.Equal('\0', after.GetPieceSymbol(Square("d5")));
        Assert.Equal(ChessSquareHighlights.Capture, after.GetHighlights(Square("d5")));
        Assert.Equal(ChessSquareHighlights.LastMove, after.GetHighlights(Square("d6")));
    }

    [Theory]
    [InlineData('w', "e1g1", "h1", "f1")]
    [InlineData('w', "e1c1", "a1", "d1")]
    [InlineData('b', "e8g8", "h8", "f8")]
    [InlineData('b', "e8c8", "a8", "d8")]
    public async Task Castling_ReportsBothRookSquares(char side, string move, string rookFrom, string rookTo)
    {
        using var match = new UciPlayableMatchSession();
        await Load(match, $"r3k2r/8/8/8/8/8/8/R3K2R {side} KQkq - 0 1");
        match.ApplyMove(move);
        var state = match.Refresh();
        var board = ChessBoardPresentation.FromMatch(in state);
        Assert.Equal(Square(rookFrom), board.LastMove!.Value.CastlingRookFrom);
        Assert.Equal(Square(rookTo), board.LastMove.Value.CastlingRookTo);
        Assert.Equal(ChessSquareHighlights.CastlingRook, board.GetHighlights(Square(rookFrom)));
        Assert.Equal(ChessSquareHighlights.CastlingRook, board.GetHighlights(Square(rookTo)));
        Assert.Equal(side == 'w' ? 'R' : 'r', board.GetPieceSymbol(Square(rookTo)));
    }

    [Fact]
    public async Task PendingPromotionAndUnderpromotion_AreReflectedImmediately()
    {
        using var match = new UciPlayableMatchSession();
        await Load(match, "7k/P7/8/8/8/8/8/7K w - - 0 1");
        match.ApplyMove("a7a8");
        var state = match.Refresh();
        var pending = ChessBoardPresentation.FromMatch(in state);
        Assert.Equal(Square("a7"), pending.SelectedSquare);
        Assert.Equal(ChessSquareHighlights.Promotion, pending.GetHighlights(Square("a8")));
        Assert.Equal('P', pending.GetPieceSymbol(Square("a7")));
        match.ChoosePromotion('n');
        state = match.Refresh();
        var completed = ChessBoardPresentation.FromMatch(in state);
        Assert.Equal('N', completed.GetPieceSymbol(Square("a8")));
        Assert.Equal('N', completed.LastMove!.Value.PromotionPiece);
        Assert.True((completed.GetHighlights(Square("a8")) & ChessSquareHighlights.Promotion) != 0);
    }

    [Fact]
    public async Task PromotionCapture_RetainsVictimAndChosenPiece()
    {
        using var match = new UciPlayableMatchSession();
        await Load(match, "1r5k/P7/8/8/8/8/8/7K w - - 0 1");
        match.ApplyMove("a7b8b");
        var state = match.Refresh();
        var board = ChessBoardPresentation.FromMatch(in state);
        Assert.Equal('B', board.LastMove!.Value.PromotionPiece);
        Assert.Equal('r', board.LastMove.Value.CapturedPiece);
        Assert.Equal(Square("b8"), board.LastMove.Value.CapturedSquare);
    }

    [Theory]
    [InlineData("4k3/8/8/8/8/8/4r3/4K3 w - - 0 1", "e1", ChessSquareHighlights.Check)]
    [InlineData("4k3/4R3/8/8/8/8/8/4K3 b - - 0 1", "e8", ChessSquareHighlights.Check)]
    [InlineData("7k/6Q1/7K/8/8/8/8/8 b - - 0 1", "h8", ChessSquareHighlights.Checkmate)]
    [InlineData("7k/5Q2/7K/8/8/8/8/8 b - - 0 1", "h8", ChessSquareHighlights.None)]
    public async Task LoadedPosition_DetectsCheckOrMateWithoutMoveHistory(string fen, string king,
        ChessSquareHighlights expected)
    {
        using var match = new UciPlayableMatchSession();
        await Load(match, fen);
        var state = match.Refresh();
        var board = ChessBoardPresentation.FromMatch(in state, Square(king));
        Assert.Equal(expected, board.GetHighlights(Square(king)) &
            (ChessSquareHighlights.Check | ChessSquareHighlights.Checkmate));
        if (state.Result.IsTerminal) Assert.Null(board.SelectedSquare);
    }

    [Fact]
    public void Undo_ReconcilesPiecesAndLastMove_WithoutChangingRetainedSnapshots()
    {
        using var match = new UciPlayableMatchSession();
        Play(match, "e2e4", "d7d5", "e4d5");
        var state = match.Refresh();
        var captured = ChessBoardPresentation.FromMatch(in state);
        match.UndoMoves();
        state = match.Refresh();
        var undone = ChessBoardPresentation.FromMatch(in state);
        Assert.Equal('p', undone.GetPieceSymbol(Square("d5")));
        Assert.Equal('P', undone.GetPieceSymbol(Square("e4")));
        Assert.Null(undone.LastMove!.Value.CapturedSquare);
        Assert.Equal(Square("d7"), undone.LastMove.Value.From);
        Assert.Equal('P', captured.GetPieceSymbol(Square("d5")));
        match.UndoMoves(2);
        state = match.Refresh();
        Assert.Null(ChessBoardPresentation.FromMatch(in state).LastMove);
    }

    [Fact]
    public void DefaultMatch_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => ChessBoardPresentation.FromMatch(default));
    }

    [Fact]
    public void RepeatedProjectionAndQueries_AllocateNothingAfterWarmup()
    {
        using var match = new UciPlayableMatchSession();
        Play(match, "e2e4");
        var state = match.Refresh();
        var selected = Square("e7");
        for (var i = 0; i < 200; i++) ChessBoardPresentation.FromMatch(in state, selected);
        long before = GC.GetAllocatedBytesForCurrentThread();
        char lastPiece = '\0';
        for (var i = 0; i < 1000; i++)
        {
            var board = ChessBoardPresentation.FromMatch(in state, selected);
            lastPiece = board.GetPieceSymbol(selected);
            board.GetHighlights(selected);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal('p', lastPiece);
        Assert.Equal(0, allocated);
    }

    private static ChessSquare Square(string value)
    {
        Assert.True(ChessSquare.TryParse(value, out var square));
        return square;
    }

    private static Task Load(UciPlayableMatchSession match, string fen) =>
        match.LoadMatchAsync(new PlayableMatchSetup(Fen.Parse(fen)!.Value));

    private static void Play(UciPlayableMatchSession match, params string[] moves)
    {
        foreach (string move in moves)
        {
            match.Refresh();
            match.ApplyMove(move);
        }
    }
}

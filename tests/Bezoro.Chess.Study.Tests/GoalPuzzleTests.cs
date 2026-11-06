using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Xunit;
using static Bezoro.Chess.Study.Tests.GuidedLessonTests;

namespace Bezoro.Chess.Study.Tests;

public sealed class GoalPuzzleTests
{
    private static readonly Fen MateThree = Fen.Parse("7k/8/8/5K2/8/8/Q7/8 w - - 0 1")!.Value;

    [Theory]
    [InlineData("7k/8/8/8/8/8/8/K7 w - - 0 1")]
    [InlineData("7k/8/8/5K2/8/8/Q7/8 w - - 150 1")]
    public void AutomaticDrawsCannotStartACheckmatePuzzle(string fen)
    {
        var player = new GuidedLessonPlayer();
        Assert.False(player.Start(Puzzle(new(Fen.Parse(fen)!.Value), 3), out string error));
        Assert.NotEmpty(error);
        Assert.Equal(GuidedLessonPhase.Inactive, player.Phase);
    }

    [Fact]
    public void AutomaticDrawAfterALearnerMoveFailsInsteadOfRequestingAnEngineReply()
    {
        var start = Fen.Parse("7k/8/8/5K2/8/8/Q7/8 w - - 149 1")!.Value;
        var player = new GuidedLessonPlayer();
        Assert.True(player.Start(Puzzle(new(start), 3), out _));
        Assert.True(player.EvaluateMoveRequest("a2a7").CanSubmitMove);
        var result = player.CompleteAcceptedMove(Position(start, "a2a7"));
        Assert.False(result.NeedsOpponentMove);
        Assert.Equal(GuidedLessonPhase.Failed, player.Phase);
        Assert.Null(player.Completion);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(4, false)]
    public void RepetitionUsesMandatoryResultsRatherThanOptionalDrawClaims(int cycles, bool canStart)
    {
        var moves = Enumerable.Repeat(new[] { "a2a3", "h8g8", "a3a2", "g8h8" }, cycles).SelectMany(cycle => cycle).ToArray();
        var player = new GuidedLessonPlayer();
        Assert.Equal(canStart, player.Start(Puzzle(new(MateThree, moves), 20), out _));
    }

    [Fact]
    public void AHistoryContinuingBeyondAnAutomaticDrawCannotStartAPuzzle()
    {
        var start = Fen.Parse("7k/8/8/5K2/8/8/Q7/8 w - - 149 1")!.Value;
        var player = new GuidedLessonPlayer();
        Assert.False(player.Start(Puzzle(new(start, new[] { "a2a7", "h8g8" }), 3), out _));
        Assert.Equal(GuidedLessonPhase.Inactive, player.Phase);
    }

    [Fact]
    public void FivefoldRepetitionAfterAnOpponentReplyFailsAndUndoResumesTheDecision()
    {
        var moves = Enumerable.Repeat(new[] { "a2a3", "h8g8", "a3a2", "g8h8" }, 3)
            .SelectMany(cycle => cycle).Concat(new[] { "a2a3", "h8g8" }).ToArray();
        var player = new GuidedLessonPlayer();
        Assert.True(player.Start(Puzzle(new(MateThree, moves), 3), out _));
        Assert.True(player.EvaluateMoveRequest("a3a2").CanSubmitMove);
        Assert.True(player.CompleteAcceptedMove(Position(MateThree, [.. moves, "a3a2"])).NeedsOpponentMove);
        var result = player.CompleteOpponentReply(Position(MateThree, [.. moves, "a3a2", "g8h8"]));
        Assert.False(result.NeedsOpponentMove);
        Assert.Equal(GuidedLessonPhase.Failed, player.Phase);
        Assert.Null(player.Completion);
        Assert.True(player.ObserveUndo(Position(MateThree, moves)));
        Assert.Equal(GuidedLessonPhase.ReadyForPlayerMove, player.Phase);
    }

    [Fact]
    public void CheckmateTakesPrecedenceOverTheSeventyFiveMoveDraw()
    {
        var start = Fen.Parse("7k/8/5KQ1/8/8/8/8/8 w - - 149 1")!.Value;
        var player = new GuidedLessonPlayer();
        Assert.True(player.Start(Puzzle(new(start), 1), out _));
        Assert.True(player.EvaluateMoveRequest("g6g7").CanSubmitMove);
        Assert.True(player.CompleteAcceptedMove(Position(start, "g6g7")).IsLessonComplete);
        Assert.NotNull(player.Completion);
    }

    [Theory]
    [InlineData("7k/8/5KQ1/8/8/8/8/8 w - - 0 1", "g6g7")]
    [InlineData("8/8/8/8/8/5kq1/8/7K b - - 0 1", "g3g2")]
    public void AGoalAcceptsLegalSolutionsForEitherSideAndIssuesProofOnlyAtMate(string fen, string solution)
    {
        var start = Fen.Parse(fen)!.Value;
        var player = new GuidedLessonPlayer();
        var definition = Puzzle(new(start), 1);
        Assert.True(player.Start(definition, out string error), error);
        Assert.Null(player.Project().RequiredMove);
        Assert.Equal(start.GetLegalMoves().Order(), player.Project().GetAllowedMoves(start.GetLegalMoves()).Order());
        Assert.True(player.EvaluateMoveRequest(solution).CanSubmitMove);
        Assert.Null(player.Completion);
        Assert.True(player.CompleteAcceptedMove(Position(start, solution)).IsLessonComplete);
        Assert.Equal(1, player.Project().LearnerMoves);
        Assert.Equal(definition.Fingerprint, player.Completion!.DefinitionFingerprint);
    }

    [Fact]
    public void ThreeLearnerMovesCountNoSetupHistoryAndNoOpponentMoves()
    {
        var player = new GuidedLessonPlayer();
        Assert.True(player.Start(Puzzle(new(MateThree), 3), out _));
        Assert.True(player.EvaluateMoveRequest("a2a7").CanSubmitMove);
        Assert.True(player.CompleteAcceptedMove(Position(MateThree, "a2a7")).NeedsOpponentMove);
        Assert.Equal(1, player.Project().LearnerMoves);
        Assert.False(player.Project().AcceptsUndoRequests);
        Assert.Empty(player.Project().GetAllowedMoves(Position(MateThree, "a2a7").Position.GetLegalMoves()));
        player.CompleteOpponentReply(Position(MateThree, "a2a7", "h8g8"));
        Assert.Equal(1, player.Project().LearnerMoves);
        Assert.Equal(2, player.Project().GetUndoPlyCount(1));
        Assert.True(player.EvaluateMoveRequest("f5g6").CanSubmitMove);
        player.CompleteAcceptedMove(Position(MateThree, "a2a7", "h8g8", "f5g6"));
        player.CompleteOpponentReply(Position(MateThree, "a2a7", "h8g8", "f5g6", "g8f8"));
        Assert.True(player.EvaluateMoveRequest("a7f7").CanSubmitMove);
        Assert.True(player.CompleteAcceptedMove(Position(MateThree, "a2a7", "h8g8", "f5g6", "g8f8", "a7f7")).IsLessonComplete);
        Assert.Equal(3, player.Project().LearnerMoves);

        var loaded = new PlayableMatchSetup(MateThree, new[] { "a2a7", "h8g8" });
        Assert.True(player.Start(Puzzle(loaded, 2), out _));
        Assert.Equal(0, player.Project().LearnerMoves);
        Assert.True(player.EvaluateMoveRequest("f5g6").CanSubmitMove);
        player.CompleteAcceptedMove(Position(MateThree, "a2a7", "h8g8", "f5g6"));
        player.CompleteOpponentReply(Position(MateThree, "a2a7", "h8g8", "f5g6", "g8f8"));
        Assert.True(player.EvaluateMoveRequest("a7f7").CanSubmitMove);
        Assert.True(player.CompleteAcceptedMove(Position(MateThree, "a2a7", "h8g8", "f5g6", "g8f8", "a7f7")).IsLessonComplete);
        Assert.Equal(2, player.Project().LearnerMoves);
    }

    [Fact]
    public void NoopsRepeatedUpdatesAndOutOfOrderMovesCannotSpendTheBudgetOrAdvance()
    {
        var player = new GuidedLessonPlayer();
        Assert.True(player.Start(Puzzle(new(MateThree), 3), out _));
        Assert.True(player.EvaluateMoveRequest("a2a7").CanSubmitMove);
        Assert.False(player.ObserveAcceptedMove(Position(MateThree)).DidAdvance);
        Assert.False(player.ObserveAcceptedMove(Position(MateThree, "a2a3")).DidAdvance);
        Assert.Equal(0, player.Project().LearnerMoves);
        Assert.Equal(GuidedLessonPhase.WaitingForMoveResult, player.Phase);
        Assert.True(player.ObserveAcceptedMove(Position(MateThree, "a2a7")).NeedsOpponentMove);
        player.ObserveAcceptedMove(Position(MateThree, "a2a7"));
        Assert.Equal(1, player.Project().LearnerMoves);
        Assert.Null(player.Completion);
        Assert.False(player.CompleteOpponentReply(Position(MateThree, "a2a7")).DidAdvance);
        Assert.Equal(GuidedLessonPhase.Failed, player.Phase);
        Assert.Null(player.Completion);
    }

    [Fact]
    public void BudgetFailureCountsOnlyConfirmedLegalLearnerMoves()
    {
        var player = new GuidedLessonPlayer();
        Assert.True(player.Start(Puzzle(new(MateThree), 1), out _));
        Assert.False(player.EvaluateMoveRequest("a2a9").CanSubmitMove);
        Assert.Equal(0, player.Project().LearnerMoves);
        Assert.True(player.EvaluateMoveRequest("a2a7").CanSubmitMove);
        var result = player.CompleteAcceptedMove(Position(MateThree, "a2a7"));
        Assert.False(result.NeedsOpponentMove);
        Assert.Equal(GuidedLessonPhase.Failed, player.Phase);
        Assert.Equal(1, player.Project().LearnerMoves);
        Assert.Null(player.Completion);
    }

    [Fact]
    public void UndoRecomputesBudgetFromARealPrefixAndRevokesOldCompletion()
    {
        var start = Fen.Parse("7k/8/5KQ1/8/8/8/8/8 w - - 0 1")!.Value;
        var player = new GuidedLessonPlayer();
        Assert.True(player.Start(Puzzle(new(start), 3), out _));
        Assert.True(player.EvaluateMoveRequest("g6g7").CanSubmitMove);
        Assert.True(player.CompleteAcceptedMove(Position(start, "g6g7")).IsLessonComplete);
        var oldProof = player.Completion;
        Assert.Equal(1, player.Project().GetUndoPlyCount(1));
        Assert.True(player.ObserveUndo(Position(start)));
        Assert.Equal(GuidedLessonPhase.ReadyForPlayerMove, player.Phase);
        Assert.Null(player.Completion);
        Assert.Equal(0, player.Project().LearnerMoves);
        Assert.False(player.ObserveUndo(Position(start)));
        Assert.False(player.ObserveUndo(Position(Fen.Default)));
        Assert.NotNull(oldProof);
        player.Reset();
        Assert.Null(player.Completion);
        Assert.Equal(0, player.Project().LearnerMoves);
    }

    [Theory]
    [InlineData("7k/6Q1/5K2/8/8/8/8/8 b - - 0 1")]
    [InlineData("7k/5K2/6Q1/8/8/8/8/8 b - - 0 1")]
    public void GoalAuthoringRejectsAlreadyTerminalPositions(string fen)
    {
        var definition = Puzzle(new(Fen.Parse(fen)!.Value), 3);
        var result = GuidedLessonValidator.Validate(definition);
        Assert.Contains("Lesson puzzle must start in a non-terminal position.", result.Messages);
        var player = new GuidedLessonPlayer();
        Assert.False(player.Start(definition, out _));
        Assert.Null(player.Completion);
    }

    [Fact]
    public void PuzzleModeAndBossKindAreIndependentAndBudgetValidationIsPortable()
    {
        var definition = Puzzle(new(MateThree), 0, GuidedLessonKind.Boss);
        Assert.Equal(GuidedLessonKind.Boss, definition.Kind);
        Assert.Equal(GuidedLessonMode.GoalPuzzle, definition.Mode);
        Assert.Contains("Lesson puzzle move budget must be positive.", GuidedLessonValidator.Validate(definition).Messages);
        Assert.NotEqual(definition.Fingerprint, Puzzle(new(MateThree), 3).Fingerprint);
    }

    [Fact]
    public void GoalAllowsLegalCountercheckAndRequiresAnActualSingleReply()
    {
        var start = Fen.Parse("4k3/8/8/8/8/8/R3r3/4K3 w - - 0 1")!.Value;
        var player = new GuidedLessonPlayer();
        Assert.True(player.Start(Puzzle(new(start), 3), out _));
        Assert.False(player.EvaluateMoveRequest("a2a3").CanSubmitMove);
        Assert.True(player.EvaluateMoveRequest("a2e2").CanSubmitMove);
        Assert.True(player.CompleteAcceptedMove(Position(start, "a2e2")).NeedsOpponentMove);
        Assert.Equal(1, player.Project().LearnerMoves);
        var before = Position(start, "a2e2");
        Assert.True(before.Position.IsInCheck());
        var reply = before.Position.GetLegalMoves()[0];
        var confirmed = Position(start, "a2e2", reply);
        var forged = new GuidedLessonPosition(confirmed.History, Fen.Default);
        Assert.False(player.CompleteOpponentReply(forged).DidAdvance);
        Assert.Null(player.Completion);
        Assert.Equal(GuidedLessonPhase.Failed, player.Phase);
    }

    [Fact]
    public void BlackMultiTurnBudgetAndUndoUseTheLearnersSideRatherThanMoveParity()
    {
        var start = Fen.Parse("8/q7/8/8/5k2/8/8/7K b - - 0 1")!.Value;
        var player = new GuidedLessonPlayer();
        Assert.True(player.Start(Puzzle(new(start), 3), out _));
        Assert.True(player.EvaluateMoveRequest("a7a2").CanSubmitMove);
        Assert.True(player.CompleteAcceptedMove(Position(start, "a7a2")).NeedsOpponentMove);
        player.CompleteOpponentReply(Position(start, "a7a2", "h1g1"));
        Assert.True(player.EvaluateMoveRequest("f4g3").CanSubmitMove);
        player.CompleteAcceptedMove(Position(start, "a7a2", "h1g1", "f4g3"));
        Assert.Equal(2, player.Project().LearnerMoves);
        Assert.True(player.ObserveUndo(Position(start, "a7a2", "h1g1")));
        Assert.Equal(1, player.Project().LearnerMoves);
        Assert.Equal(GuidedLessonPhase.ReadyForPlayerMove, player.Phase);
        Assert.True(player.EvaluateMoveRequest("f4g3").CanSubmitMove);
        player.CompleteAcceptedMove(Position(start, "a7a2", "h1g1", "f4g3"));
        player.CompleteOpponentReply(Position(start, "a7a2", "h1g1", "f4g3", "g1f1"));
        Assert.True(player.EvaluateMoveRequest("a2f2").CanSubmitMove);
        Assert.True(player.CompleteAcceptedMove(Position(start, "a7a2", "h1g1", "f4g3", "g1f1", "a2f2")).IsLessonComplete);
        Assert.Equal(3, player.Project().LearnerMoves);
    }

    private static GuidedLessonDefinition Puzzle(PlayableMatchSetup setup, int moves, GuidedLessonKind kind = GuidedLessonKind.Fundamental) =>
        new("goal", "Find the mate", setup, [], kind: kind, mode: GuidedLessonMode.GoalPuzzle, puzzle: new(moves, "Find checkmate."));
}

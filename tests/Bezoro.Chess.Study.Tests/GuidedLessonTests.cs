using System.Collections.Immutable;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Xunit;

namespace Bezoro.Chess.Study.Tests;

public sealed class GuidedLessonTests
{
    [Fact]
    public void ExplanationMoveAndReplyRequireTheirCurrentPhaseAndActualSnapshots()
    {
        var lesson = Scripted(new GuidedLessonStep("Intro"), new("Move", expectedPlayerMove: "e2e4", opponentReply: "e7e5"), new("Review"));
        var player = new GuidedLessonPlayer();
        Assert.True(player.BeginLoading(lesson, out var error), error);
        Assert.False(player.ContinueExplanationStep().DidAdvance);
        Assert.False(player.CompleteOpponentReply(Position()).DidAdvance);
        Assert.False(player.EvaluateMoveRequest("e2e4").CanSubmitMove);
        player.CompleteSetupLoading();
        Assert.Equal(GuidedLessonPhase.ShowingExplanation, player.Phase);
        Assert.True(player.ContinueExplanationStep().DidAdvance);
        Assert.Equal("e2e4", player.Project().RequiredMove!.Value.Notation);
        Assert.False(player.Project().AcceptsUndoRequests);
        Assert.Equal(new[] { "e2e4" }, player.Project().GetAllowedMoves(Fen.Default.GetLegalMoves()));
        Assert.False(player.EvaluateMoveRequest("d2d4").CanSubmitMove);
        Assert.True(player.EvaluateMoveRequest("e2e4").CanSubmitMove);
        Assert.Empty(player.Project().GetAllowedMoves(Fen.Default.GetLegalMoves()));
        Assert.False(player.EvaluateMoveRequest("e2e4").CanSubmitMove);
        Assert.False(player.ObserveAcceptedMove(Position()).DidAdvance);
        Assert.Equal(GuidedLessonPhase.WaitingForMoveResult, player.Phase);
        var accepted = player.ObserveAcceptedMove(Position("e2e4"));
        Assert.Equal("e7e5", accepted.OpponentReply);
        Assert.Equal(GuidedLessonPhase.WaitingForOpponentReply, player.Phase);
        Assert.False(player.CompleteAcceptedMove(Position("e2e4")).DidAdvance);
        Assert.True(player.CompleteOpponentReply(Position("e2e4", "e7e5")).DidAdvance);
        Assert.True(player.ContinueExplanationStep().IsLessonComplete);
        Assert.Equal(1, player.Completion!.MistakeCount);
        Assert.False(player.Project().AcceptsMoveRequests);
    }

    [Theory]
    [InlineData(GuidedLessonPhase.Inactive)]
    [InlineData(GuidedLessonPhase.LoadingSetup)]
    [InlineData(GuidedLessonPhase.ShowingExplanation)]
    [InlineData(GuidedLessonPhase.ReadyForPlayerMove)]
    [InlineData(GuidedLessonPhase.WaitingForMoveResult)]
    [InlineData(GuidedLessonPhase.WaitingForOpponentReply)]
    [InlineData(GuidedLessonPhase.Completed)]
    [InlineData(GuidedLessonPhase.Failed)]
    public void ResetFromEveryPhaseReleasesInputAndCompletion(GuidedLessonPhase target)
    {
        var player = new GuidedLessonPlayer();
        var step = target == GuidedLessonPhase.ShowingExplanation ? new GuidedLessonStep("Explain")
            : new GuidedLessonStep("Move", expectedPlayerMove: "e2e4", opponentReply: target == GuidedLessonPhase.WaitingForOpponentReply ? "e7e5" : null);
        if (target != GuidedLessonPhase.Inactive)
        {
            Assert.True(player.BeginLoading(Scripted(step), out _));
            if (target != GuidedLessonPhase.LoadingSetup) player.CompleteSetupLoading();
            if (target is GuidedLessonPhase.WaitingForMoveResult or GuidedLessonPhase.WaitingForOpponentReply or GuidedLessonPhase.Completed)
                Assert.True(player.EvaluateMoveRequest("e2e4").CanSubmitMove);
            if (target is GuidedLessonPhase.WaitingForOpponentReply or GuidedLessonPhase.Completed) player.CompleteAcceptedMove(Position("e2e4"));
            if (target == GuidedLessonPhase.Failed) player.Fail("I/O failed");
        }
        Assert.Equal(target, player.Phase);
        player.Reset();
        Assert.Null(player.Completion);
        Assert.Equal(-1, player.CurrentStepIndex);
        Assert.Equal(string.Empty, player.Project().Title);
        Assert.True(player.EvaluateMoveRequest("d2d4").CanSubmitMove);
        Assert.Equal(20, player.Project().GetAllowedMoves(Fen.Default.GetLegalMoves()).Count);
        Assert.Equal(2, player.Project().GetUndoPlyCount(2));
        player.CompleteSetupLoading();
        Assert.Equal(GuidedLessonPhase.Inactive, player.Phase);
    }

    [Theory]
    [InlineData(GuidedLessonObjective.Capture)]
    [InlineData(GuidedLessonObjective.GiveCheck)]
    [InlineData(GuidedLessonObjective.EscapeCheck)]
    [InlineData(GuidedLessonObjective.Checkmate)]
    [InlineData(GuidedLessonObjective.Promote)]
    [InlineData(GuidedLessonObjective.Castle)]
    public void AMatchingMoveCannotForgeAnUnsatisfiedObjective(GuidedLessonObjective objective)
    {
        var player = new GuidedLessonPlayer();
        Assert.True(player.Start(Scripted(new GuidedLessonStep("Move", expectedPlayerMove: "e2e4", objective: objective)), out _));
        Assert.True(player.EvaluateMoveRequest("e2e4").CanSubmitMove);
        Assert.False(player.CompleteAcceptedMove(Position("e2e4")).IsLessonComplete);
        Assert.Equal(GuidedLessonPhase.Failed, player.Phase);
        Assert.Null(player.Completion);
    }

    [Theory]
    [InlineData("4r2k/8/8/8/8/8/8/4K3 w - - 1 2", "e1f1", GuidedLessonObjective.EscapeCheck)]
    [InlineData("4k3/8/8/8/8/8/R3r3/4K3 w - - 0 1", "a2e2", GuidedLessonObjective.EscapeCheck)]
    [InlineData("4k3/8/8/8/8/8/R3r3/4K3 w - - 0 1", "a2e2", GuidedLessonObjective.GiveCheck)]
    [InlineData("4k3/8/8/8/8/8/R3r3/4K3 w - - 0 1", "a2e2", GuidedLessonObjective.Capture)]
    [InlineData("7k/P7/8/8/8/8/8/7K w - - 0 1", "a7a8q", GuidedLessonObjective.Promote)]
    [InlineData("4k3/8/8/8/8/8/8/4K2R w K - 0 1", "e1g1", GuidedLessonObjective.Castle)]
    [InlineData("7k/8/5KQ1/8/8/8/8/8 w - - 0 1", "g6g7", GuidedLessonObjective.Checkmate)]
    public void ObjectivesUseSharedRulesWithoutEngineMetadata(string fen, string move, GuidedLessonObjective objective)
    {
        var start = Fen.Parse(fen)!.Value;
        Assert.True(string.IsNullOrEmpty(start.Checkers));
        var lesson = new GuidedLessonDefinition("objective", "Objective", new(start), [new("Move", expectedPlayerMove: move, objective: objective)]);
        var player = new GuidedLessonPlayer();
        Assert.True(player.Start(lesson, out var error), error);
        Assert.True(player.EvaluateMoveRequest(move).CanSubmitMove);
        var result = player.CompleteAcceptedMove(Position(start, move));
        Assert.True(result.IsLessonComplete, result.Feedback);
        Assert.Equal(lesson.Fingerprint, player.Completion!.DefinitionFingerprint);
    }

    [Fact]
    public void PromotionChoicesRemainExactWhileThePhysicalChoiceIsPending()
    {
        var start = Fen.Parse("7k/P7/8/8/8/8/8/7K w - - 0 1")!.Value;
        var player = new GuidedLessonPlayer();
        Assert.True(player.Start(new("promotion", "Promote", new(start), [new(expectedPlayerMove: "a7a8q")]), out _));
        Assert.True(player.EvaluateMoveRequest(" A7A8 ").CanSubmitMove);
        Assert.Equal(new[] { 'q' }, player.Project().AllowedPromotionPieces);
        Assert.True(player.AcceptsPromotion('Q'));
        Assert.False(player.AcceptsPromotion('r'));
        Assert.Null(player.Project().RequiredMove);
        Assert.False(player.CompleteAcceptedMove(Position(start, "a7a8r")).IsLessonComplete);
        Assert.Null(player.Completion);
    }

    [Fact]
    public void UnappliedInconsistentAndUnknownReplySnapshotsNeverIssueAReceipt()
    {
        var player = new GuidedLessonPlayer();
        var lesson = Scripted(new GuidedLessonStep(expectedPlayerMove: "e2e4", opponentReply: "e7e5"));
        Assert.True(player.Start(lesson, out _));
        Assert.True(player.EvaluateMoveRequest("e2e4").CanSubmitMove);
        Assert.False(player.CompleteAcceptedMove(new(new(Fen.Default, ["e2e4"]), Fen.Default)).IsLessonComplete);
        Assert.Null(player.Completion);
        Assert.True(player.Start(lesson, out _));
        Assert.True(player.EvaluateMoveRequest("e2e4").CanSubmitMove);
        player.CompleteAcceptedMove(Position("e2e4"));
        Assert.False(player.CompleteOpponentReply(Position("e2e4")).DidAdvance);
        Assert.Equal(GuidedLessonPhase.Failed, player.Phase);
        Assert.Null(player.Completion);
    }

    [Fact]
    public void DefinitionsAttemptsReceiptsAndResetsRemainIsolated()
    {
        var steps = new List<GuidedLessonStep> { new(expectedPlayerMove: "e2e4") };
        var lesson = new GuidedLessonDefinition("one", "One", PlayableMatchSetup.Standard, steps);
        steps[0] = new(expectedPlayerMove: "d2d4");
        var first = new GuidedLessonPlayer(); var second = new GuidedLessonPlayer();
        Assert.True(first.Start(lesson, out _)); Assert.True(second.Start(lesson, out _));
        Assert.True(first.EvaluateMoveRequest("e2e4").CanSubmitMove);
        Assert.True(first.CompleteAcceptedMove(Position("e2e4")).IsLessonComplete);
        var receipt = first.Completion!;
        first.Reset();
        Assert.Null(first.Completion);
        Assert.Equal(GuidedLessonPhase.ReadyForPlayerMove, second.Phase);
        Assert.True(second.EvaluateMoveRequest("e2e4").CanSubmitMove);
        second.CompleteAcceptedMove(Position("e2e4"));
        Assert.NotEqual(receipt.AttemptId, second.Completion!.AttemptId);
        Assert.Equal(receipt.DefinitionFingerprint, second.Completion.DefinitionFingerprint);
        Assert.True(first.Start(lesson, out _)); Assert.Equal(string.Empty, first.Project().Feedback);
        Assert.Empty(typeof(ChessLessonCompletion).GetConstructors());
    }

    [Fact]
    public void ScriptedConfirmationsCannotRebaseHistoryOrBypassThePendingBaseline()
    {
        var player = new GuidedLessonPlayer();
        var definition = Scripted(new("White", expectedPlayerMove: "e2e4"), new("Black", expectedPlayerMove: "e7e5"));
        Assert.True(player.Start(definition, out _));
        Assert.True(player.EvaluateMoveRequest("e2e4").CanSubmitMove);
        player.CompleteAcceptedMove(Position("e2e4"));
        Assert.True(player.EvaluateMoveRequest("e7e5").CanSubmitMove);
        var afterWhite = Position("e2e4").Position;
        Assert.False(player.CompleteAcceptedMove(Position(afterWhite, "e7e5")).IsLessonComplete);
        Assert.Equal(GuidedLessonPhase.Failed, player.Phase);
        Assert.Null(player.Completion);

        Assert.True(player.Start(Scripted(new GuidedLessonStep(expectedPlayerMove: "e2e4")), out _));
        Assert.True(player.EvaluateMoveRequest("e2e4", playedMoveCount: 2).CanSubmitMove);
        Assert.False(player.CompleteAcceptedMove(Position("e2e4")).IsLessonComplete);
        Assert.Null(player.Completion);
    }

    [Fact]
    public void DelayedUnrelatedSnapshotWithTheSameLastMoveDoesNotFailANewAttempt()
    {
        var player = new GuidedLessonPlayer();
        Assert.True(player.Start(Scripted(new GuidedLessonStep(expectedPlayerMove: "e2e4")), out _));
        Assert.True(player.EvaluateMoveRequest("e2e4").CanSubmitMove);
        Assert.False(player.ObserveAcceptedMove(Position("g1f3", "g8f6", "e2e4")).DidAdvance);
        Assert.Equal(GuidedLessonPhase.WaitingForMoveResult, player.Phase);
        Assert.Null(player.Completion);
        Assert.True(player.ObserveAcceptedMove(Position("e2e4")).IsLessonComplete);
    }

    [Fact]
    public void ValidationAndAuthoringPreviewReplayWholeSequenceAndCatalogIds()
    {
        var lesson = Scripted(new GuidedLessonStep("Move", expectedPlayerMove: "e2e4", opponentReply: "e7e5"), new("Develop", expectedPlayerMove: "g1f3"));
        Assert.False(GuidedLessonValidator.Validate(lesson).HasErrors);
        Assert.Equal(Position("e2e4", "e7e5").Position, lesson.GetPositionBeforeStep(1));
        var duplicate = new GuidedLessonDefinition(" LESSON ", "Duplicate", PlayableMatchSetup.Standard, [new("Intro")]);
        Assert.Contains("Duplicate lesson id 'lesson'.", GuidedLessonCatalog.Validate([lesson, duplicate]).Messages);
        var invalid = Scripted(new GuidedLessonStep(expectedPlayerMove: "e2e5"));
        Assert.Contains("Lesson step 1 expected player move is not legal for the lesson position.", GuidedLessonValidator.Validate(invalid).Messages);
        Assert.Contains("Lesson initial setup: bad board", GuidedLessonValidator.Validate(lesson, new[] { "bad board" }).Messages);
        Assert.NotEqual(lesson.Fingerprint, duplicate.Fingerprint);
    }

    private static GuidedLessonDefinition Scripted(params GuidedLessonStep[] steps) => new("lesson", "Lesson", PlayableMatchSetup.Standard, steps);
    internal static GuidedLessonPosition Position(params string[] moves) => Position(Fen.Default, moves);
    internal static GuidedLessonPosition Position(Fen start, params string[] moves)
    {
        var setup = new PlayableMatchSetup(start, moves);
        return new(setup, setup.ResolveCurrentFen());
    }
}

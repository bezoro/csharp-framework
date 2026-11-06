using Bezoro.Chess.Study;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.Progression;
using Xunit;

namespace Bezoro.Chess.Study.Tests;

public sealed class LearningCampaignTests
{
    public static IEnumerable<object[]> ScriptedStages() => ChessLearningCampaign.CreateFoundations().Stages
        .Where(stage => stage.Lesson.Mode == GuidedLessonMode.Scripted).Select(stage => new object[] { stage.Lesson.Id });

    [Fact]
    public void FoundationsHasStableCumulativeStagesAndIndependentBossModes()
    {
        var campaign = ChessLearningCampaign.CreateFoundations();
        Assert.Equal("chess-foundations-v1", campaign.Id);
        Assert.Equal(17, campaign.Stages.Count);
        Assert.Equal(new[] { 4, 8, 12, 17 }, campaign.Stages.Where(stage => stage.Lesson.Kind == GuidedLessonKind.Boss)
            .Select(stage => stage.Lesson.Order));
        Assert.Equal(3, campaign.Stages.Count(stage => stage.Lesson.Kind == GuidedLessonKind.Boss && stage.Lesson.Mode == GuidedLessonMode.Scripted));
        var goal = Assert.Single(campaign.Stages, stage => stage.Lesson.Mode == GuidedLessonMode.GoalPuzzle);
        Assert.Equal("foundations.boss-checkmate", goal.Lesson.Id);
        Assert.Equal(3, goal.Lesson.Puzzle!.MaxLearnerMoves);
        for (int index = 0; index < campaign.Stages.Count; index++)
        {
            var stage = campaign.Stages[index];
            Assert.Equal((uint)index + 1, stage.Id.Value);
            Assert.Equal(index + 1, stage.Lesson.Order);
            Assert.Equal(1, stage.Progression.MaximumStars);
            Assert.Same(stage, campaign.GetStage(stage.Id));
            Assert.Same(stage, campaign.GetStage(stage.Lesson.Id));
            Assert.Same(stage.Lesson.Fundamentals, stage.ReinforcedFundamentals);
        }
    }

    [Theory]
    [MemberData(nameof(ScriptedStages))]
    public void EveryScriptedPhaseRejectsOtherMovesAndCompletesOnlyItsLegalAuthoredSequence(string lessonId)
    {
        var lesson = ChessLearningCampaign.CreateFoundations().GetStage(lessonId).Lesson;
        var validation = GuidedLessonValidator.Validate(lesson);
        Assert.False(validation.HasErrors, string.Join("\n", validation.Messages));
        var player = new GuidedLessonPlayer();
        Assert.True(player.Start(lesson, out string error), error);
        var setup = lesson.InitialSetup!.Value;
        var moves = setup.PlayedMoves.ToList();
        var position = setup.ResolveCurrentFen();

        foreach (var step in lesson.Steps)
        {
            Assert.Equal(GuidedLessonPhase.ReadyForPlayerMove, player.Phase);
            var projection = player.Project();
            Assert.NotNull(projection.RequiredMove);
            Assert.Equal(UciCoordinateMove.TryNormalize(step.ExpectedPlayerMove, out string normalized) ? normalized : null,
                projection.RequiredMove.Value.Notation);
            Assert.Equal(new[] { normalized }, projection.GetAllowedMoves(position.GetLegalMoves()));
            string wrong = position.GetLegalMoves().First(move => !GuidedLessonMoves.IsExpected(normalized, move, false));
            string before = position.Raw;
            int beforeCount = moves.Count;
            Assert.False(player.EvaluateMoveRequest(wrong, moves.Count).CanSubmitMove);
            Assert.Equal(GuidedLessonPhase.ReadyForPlayerMove, player.Phase);
            Assert.Equal(before, position.Raw);
            Assert.Equal(beforeCount, moves.Count);
            Assert.Equal(projection.RequiredMove, player.Project().RequiredMove);
            Assert.Null(player.Completion);

            Assert.True(player.EvaluateMoveRequest(normalized, moves.Count).CanSubmitMove);
            position = position.ApplyMove(normalized);
            moves.Add(normalized);
            var applied = player.CompleteAcceptedMove(Snapshot(setup.BaseFen, moves, position));
            Assert.NotEqual(GuidedLessonPhase.Failed, player.Phase);
            if (step.OpponentReply.Length == 0) continue;
            Assert.Equal(GuidedLessonPhase.WaitingForOpponentReply, player.Phase);
            Assert.Equal(step.OpponentReply, applied.OpponentReply);
            position = position.ApplyMove(step.OpponentReply);
            moves.Add(step.OpponentReply);
            player.CompleteOpponentReply(Snapshot(setup.BaseFen, moves, position));
            Assert.NotEqual(GuidedLessonPhase.Failed, player.Phase);
        }
        Assert.Equal(GuidedLessonPhase.Completed, player.Phase);
        Assert.NotNull(player.Completion);
        Assert.Equal(lesson.Steps.Count, player.MistakeCount);
        Assert.False(player.EvaluateMoveRequest(position.GetLegalMoves().FirstOrDefault() ?? "a1a2", moves.Count).CanSubmitMove);
    }

    [Fact]
    public void GoalBossAllowsAnyLegalMoveAndDoesNotPublishARequiredArrow()
    {
        var lesson = ChessLearningCampaign.CreateFoundations().GetStage("foundations.boss-checkmate").Lesson;
        var player = new GuidedLessonPlayer();
        Assert.True(player.Start(lesson, out string error), error);
        var position = lesson.InitialSetup!.Value.ResolveCurrentFen();
        var projection = player.Project();
        Assert.Null(projection.RequiredMove);
        Assert.Equal(position.GetLegalMoves(), projection.GetAllowedMoves(position.GetLegalMoves()));
        Assert.DoesNotContain("a2a3", new[] { "a2a7", "f5g6", "a7f7" });
        foreach (string move in position.GetLegalMoves())
        {
            var attempt = new GuidedLessonPlayer();
            Assert.True(attempt.Start(lesson, out error), error);
            Assert.True(attempt.EvaluateMoveRequest(move).CanSubmitMove, move);
        }
    }

    [Fact]
    public void GoalBossCompletesTheVerifiedMateInThreeLineWithConfirmedOpponentReplies()
    {
        var lesson = ChessLearningCampaign.CreateFoundations().GetStage("foundations.boss-checkmate").Lesson;
        var player = new GuidedLessonPlayer();
        Assert.True(player.Start(lesson, out string error), error);
        var setup = lesson.InitialSetup!.Value;
        var position = setup.ResolveCurrentFen();
        var moves = setup.PlayedMoves.ToList();
        // Stockfish 19, depth 20: mate 3, PV a2a7 h8g8 f5g6 g8f8 a7f7.
        string[] line = { "a2a7", "h8g8", "f5g6", "g8f8", "a7f7" };
        for (int index = 0; index < line.Length; index++)
        {
            string move = line[index];
            bool learner = index % 2 == 0;
            if (learner) Assert.True(player.EvaluateMoveRequest(move, moves.Count).CanSubmitMove);
            position = position.ApplyMove(move);
            moves.Add(move);
            var confirmation = Snapshot(setup.BaseFen, moves, position);
            var result = learner ? player.CompleteAcceptedMove(confirmation) : player.CompleteOpponentReply(confirmation);
            Assert.NotEqual(GuidedLessonPhase.Failed, player.Phase);
            if (learner && index < line.Length - 1) Assert.True(result.NeedsOpponentMove);
            if (index < line.Length - 1) Assert.Null(player.Completion);
        }
        Assert.True(position.IsInCheck());
        Assert.Empty(position.GetLegalMoves());
        Assert.Equal(3, player.Project().LearnerMoves);
        Assert.Equal(GuidedLessonPhase.Completed, player.Phase);
        Assert.NotNull(player.Completion);
    }

    [Fact]
    public void MateThreeBossAuthoredStrategyCoversEveryLegalOpponentDefense()
    {
        var lesson = ChessLearningCampaign.CreateFoundations().GetStage("foundations.boss-checkmate").Lesson;
        var position = lesson.InitialSetup!.Value.ResolveCurrentFen().ApplyMove("a2a7");
        Assert.Equal(new[] { "h8g8" }, position.GetLegalMoves());
        position = position.ApplyMove("h8g8").ApplyMove("f5g6");
        Assert.Equal(new[] { "g8f8", "g8h8" }, position.GetLegalMoves().OrderBy(move => move));
        foreach (string defense in position.GetLegalMoves())
        {
            string finish = defense == "g8f8" ? "a7f7" : "a7h7";
            var mate = position.ApplyMove(defense).ApplyMove(finish);
            Assert.True(mate.IsInCheck());
            Assert.Empty(mate.GetLegalMoves());
        }
    }

    [Fact]
    public void StoryUnlocksOnlyAfterEachEarlierPhaseAndBossCompletion()
    {
        var campaign = ChessLearningCampaign.CreateFoundations();
        var progress = new StoryProgress(campaign.Story);
        for (int current = 0; current < campaign.Stages.Count; current++)
        {
            for (int later = current + 1; later < campaign.Stages.Count; later++)
            {
                Assert.False(progress.IsUnlocked(campaign.Stages[later].Id));
                Assert.False(progress.TryComplete(campaign.Stages[later].Id, 1, out _));
            }
            Assert.True(progress.IsUnlocked(campaign.Stages[current].Id));
            Assert.True(progress.TryComplete(campaign.Stages[current].Id, 1, out var completion));
            Assert.True(completion.IsFirstCompletion);
        }
    }

    [Theory]
    [InlineData("unknown", false)]
    [InlineData("second", false)]
    [InlineData("first", true)]
    public void BossMustReinforceAFundamentalInItsActualPrerequisiteClosure(string reinforced, bool valid)
    {
        var first = Stage(1, "first");
        var second = Stage(2, "second");
        var boss = new ChessLearningStage(new ProgressionId(3), Lesson("boss", GuidedLessonKind.Boss, reinforced),
            new[] { new StoryPrerequisite(first.Id) });
        if (valid)
            Assert.Equal(3, new ChessLearningCampaign("sample", new[] { first, second, boss }).Stages.Count);
        else
            Assert.Throws<ArgumentException>(() => new ChessLearningCampaign("sample", new[] { first, second, boss }));
    }

    [Fact]
    public void CampaignCopiesStagesAndRejectsAmbiguousLessonIds()
    {
        var first = Stage(1, "first");
        var input = new[] { first };
        var campaign = new ChessLearningCampaign("sample", input);
        input[0] = Stage(2, "second");
        Assert.Same(first, Assert.Single(campaign.Stages));
        Assert.Same(first, campaign.GetStage(" FIRST "));
        Assert.Throws<ArgumentException>(() => new ChessLearningCampaign("sample", new[] { first, Stage(2, " FIRST ") }));
    }

    private static GuidedLessonPosition Snapshot(Fen baseFen, IEnumerable<string> moves, Fen position) => new(new PlayableMatchSetup(baseFen, moves), position);
    private static ChessLearningStage Stage(uint number, string id) => new(new ProgressionId(number), Lesson(id));
    private static GuidedLessonDefinition Lesson(string id, GuidedLessonKind kind = GuidedLessonKind.Fundamental, string? reinforced = null) =>
        new(id, id, new PlayableMatchSetup(Fen.Default), new[] { new GuidedLessonStep(expectedPlayerMove: "e2e4") },
            kind: kind, fundamentals: reinforced is null ? null : new[] { reinforced });
}

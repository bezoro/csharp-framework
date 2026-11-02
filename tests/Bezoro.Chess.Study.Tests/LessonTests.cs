using Bezoro.Chess.UCI.Protocol.API.Types;
using Xunit;

namespace Bezoro.Chess.Study.Tests;

public sealed class LessonTests
{
    [Fact]
    public void LessonRejectsWrongMovesAutomaticallyRepliesAndUndoesWholeDecision()
    {
        var definition = new ChessLesson(Fen.Default, new[]
        {
            new[] { "e2e4", "e7e5", "g1f3" },
            new[] { "d2d4", "d7d5", "c2c4" }
        });
        var attempt = definition.StartAttempt();
        Assert.Equal(2, attempt.HintCount);
        Assert.False(attempt.TryPlay("f2f3").Accepted);
        Assert.Equal(Fen.Default, attempt.Position);
        var result = attempt.TryPlay("e2e4");
        Assert.True(result.Accepted);
        Assert.False(result.Completed);
        Assert.Equal("e7e5", result.OpponentReply);
        Assert.Equal('w', attempt.Position.ActiveColor);
        Assert.Equal("g1f3", attempt.GetHint(0));
        Assert.True(attempt.TryPlay("g1f3").Completed);
        Assert.False(attempt.TryPlay("a7a6").Accepted);
        Assert.Equal(1, attempt.MistakeCount);
        Assert.True(attempt.Undo());
        Assert.False(attempt.IsCompleted);
        Assert.True(attempt.Undo());
        Assert.Equal(Fen.Default, attempt.Position);
        Assert.False(attempt.Undo());
        Assert.True(attempt.TryPlay("d2d4").Accepted);
        Assert.True(attempt.TryPlay("c2c4").Completed);
        attempt.Reset();
        Assert.Equal(0, attempt.MistakeCount);
        Assert.Equal(Fen.Default, attempt.Position);
        Assert.Equal(Fen.Default, definition.StartAttempt().Position);
    }

    [Fact]
    public void SingleMovePromotionExerciseSupportsBlackLearner()
    {
        var definition = new ChessLesson(Fen.Parse("4k3/8/8/8/8/8/p7/4K3 b - - 0 1")!.Value,
            new[] { new[] { "a2a1q" }, new[] { "a2a1r" } });
        var attempt = definition.StartAttempt();
        Assert.Equal('b', definition.LearnerSide);
        Assert.True(attempt.TryPlay("a2a1r").Completed);
        Assert.True(attempt.Undo());
        Assert.Equal(definition.StartingPosition, attempt.Position);
    }

    [Fact]
    public void MalformedOrAmbiguousSolutionsAreRejectedAtAuthoringTime()
    {
        Assert.Throws<ArgumentException>(() => new ChessLesson(Fen.Default, Array.Empty<string[]>()));
        Assert.Throws<ArgumentException>(() => new ChessLesson(Fen.Default, new[] { Array.Empty<string>() }));
        Assert.Throws<ArgumentException>(() => new ChessLesson(Fen.Default, new[] { new[] { "e2e5" } }));
        Assert.Throws<ArgumentException>(() => new ChessLesson(Fen.Default, new[] { new[] { "e2e4", "e7e5" } }));
        Assert.Throws<ArgumentException>(() => new ChessLesson(Fen.Default, new[] { new[] { "e2e4" }, new[] { "e2e4", "e7e5", "g1f3" } }));
        Assert.Throws<ArgumentException>(() => new ChessLesson(Fen.Default, new[] { new[] { "e2e4", "e7e5", "g1f3" }, new[] { "e2e4" } }));
        Assert.Throws<ArgumentException>(() => new ChessLesson(Fen.Default, new[] { new[] { "e2e4", "e7e5", "g1f3" }, new[] { "e2e4", "c7c5", "g1f3" } }));
        Assert.Throws<ArgumentNullException>(() => new ChessLesson(Fen.Default, new[] { new[] { "e2e4" } }, maxNodes: 2, maxMovesPerLine: 1).StartAttempt().TryPlay(null!));
        Assert.Throws<ArgumentException>(() => new ChessLesson(Fen.Default, new[] { new[] { "e2e4", "e7e5", "g1f3" } }, maxMovesPerLine: 2));
        Assert.Throws<ArgumentException>(() => new ChessLesson(Fen.Default, new[] { new[] { "e2e4" }, new[] { "e2e4" }, new[] { "e2e4" } }, maxNodes: 2));
    }
}

using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.UCI.Protocol.Internal;

internal static class PlayableMatchClockKernel
{
    internal static PlayableMatchClockRestore CreateRestore(
        Fen currentFen, PlayableMatchClockSetup setup,
        PlayableMatchTimeControl? timeControl,
        DateTimeOffset now)
    {
        setup.Validate();
        if (setup.ExactRestore.HasValue)
            return setup.ExactRestore.Value;
        var moveCounts = ResolveCompletedMoveCounts(currentFen);
        int activeMovesCompleted = currentFen.ActiveColor == 'w' ? moveCounts.White : moveCounts.Black;
        return new(
            setup.WhiteRemaining,
            setup.BlackRemaining,
            currentFen.ActiveColor,
            setup.DelayRemaining,
            setup.IsPaused,
            moveCounts.White,
            moveCounts.Black,
            GetStageIndex(timeControl, activeMovesCompleted),
            setup.SnapshotUtc ?? now);
    }

    internal static PlayableMatchClockCheckpoint Initialize(
        PlayableMatchTimeControl timeControl, char activeColor,
        DateTimeOffset now) =>
        new(
            timeControl.InitialTime,
            timeControl.InitialTime,
            activeColor,
            0,
            0,
            now,
            null);

    internal static PlayableMatchClockCheckpoint Restore(
        PlayableMatchTimeControl timeControl, char expectedActiveColor,
        PlayableMatchClockRestore restore)
    {
        ValidateRestore(timeControl, expectedActiveColor, restore);
        var checkpoint = new PlayableMatchClockCheckpoint(
            restore.WhiteRemaining,
            restore.BlackRemaining,
            restore.ActiveColor,
            restore.WhiteMovesCompleted,
            restore.BlackMovesCompleted,
            restore.SnapshotUtc,
            null);
        var stage = GetStage(timeControl, GetMovesCompleted(checkpoint, restore.ActiveColor));
        TimeSpan elapsedSinceTurnStart = stage.Delay - restore.DelayRemaining;
        return checkpoint with
        {
            TurnStartedAtUtc = restore.SnapshotUtc - elapsedSinceTurnStart,
            PausedAtUtc = restore.IsPaused ? restore.SnapshotUtc : null
        };
    }

    // Preserve the original turn-start amounts so a move played after restoring can still be undone exactly.
    internal static PlayableMatchClockCheckpoint RestoreWithBaseline(
        PlayableMatchTimeControl timeControl, char expectedActiveColor,
        PlayableMatchClockRestore turnStart, PlayableMatchClockRestore current)
    {
        var baseline = Restore(timeControl, expectedActiveColor, turnStart);
        Restore(timeControl, expectedActiveColor, current);
        var stage = GetStage(timeControl, GetMovesCompleted(baseline, expectedActiveColor));
        if (turnStart.DelayRemaining != stage.Delay ||
            turnStart.WhiteMovesCompleted != current.WhiteMovesCompleted ||
            turnStart.BlackMovesCompleted != current.BlackMovesCompleted ||
            turnStart.ActiveStageIndex != current.ActiveStageIndex)
            throw new ArgumentException("The live clock must match the retained turn-start checkpoint.", nameof(current));
        TimeSpan activeAtStart = expectedActiveColor == 'w' ? turnStart.WhiteRemaining : turnStart.BlackRemaining;
        TimeSpan activeNow = expectedActiveColor == 'w' ? current.WhiteRemaining : current.BlackRemaining;
        TimeSpan inactiveAtStart = expectedActiveColor == 'w' ? turnStart.BlackRemaining : turnStart.WhiteRemaining;
        TimeSpan inactiveNow = expectedActiveColor == 'w' ? current.BlackRemaining : current.WhiteRemaining;
        if (activeNow > activeAtStart || inactiveNow != inactiveAtStart ||
            activeNow < activeAtStart && current.DelayRemaining > TimeSpan.Zero)
            throw new ArgumentException("The live clock cannot increase time or charge main time before its delay expires.", nameof(current));
        TimeSpan elapsed = checked((stage.Delay - current.DelayRemaining) + (activeAtStart - activeNow));
        return baseline with
        {
            TurnStartedAtUtc = current.SnapshotUtc - elapsed,
            PausedAtUtc = current.IsPaused ? current.SnapshotUtc : null
        };
    }

    internal static PlayableMatchClockCheckpoint CompleteMove(
        PlayableMatchTimeControl timeControl, PlayableMatchClockCheckpoint checkpoint,
        char movingSide,
        DateTimeOffset now)
    {
        TimeSpan elapsed = ComputeElapsed(checkpoint, now);
        TimeSpan whiteRemaining = checkpoint.WhiteRemaining;
        TimeSpan blackRemaining = checkpoint.BlackRemaining;
        int whiteMoves = checkpoint.WhiteMovesCompleted;
        int blackMoves = checkpoint.BlackMovesCompleted;
        var stage = GetStage(timeControl, GetMovesCompleted(checkpoint, movingSide));
        TimeSpan mainElapsed = elapsed > stage.Delay ? elapsed - stage.Delay : TimeSpan.Zero;

        if (movingSide == 'w')
            (whiteRemaining, whiteMoves) = CompleteSide(
                timeControl, whiteRemaining, whiteMoves, mainElapsed, stage.Increment);
        else
            (blackRemaining, blackMoves) = CompleteSide(
                timeControl, blackRemaining, blackMoves, mainElapsed, stage.Increment);
        return new(
            whiteRemaining,
            blackRemaining,
            Opposite(movingSide),
            whiteMoves,
            blackMoves,
            now,
            checkpoint.PausedAtUtc.HasValue ? now : null);
    }

    internal static PlayableMatchClockState Snapshot(
        PlayableMatchTimeControl timeControl, PlayableMatchClockCheckpoint checkpoint,
        DateTimeOffset now)
    {
        TimeSpan elapsed = ComputeElapsed(checkpoint, now);
        TimeSpan whiteRemaining = checkpoint.WhiteRemaining;
        TimeSpan blackRemaining = checkpoint.BlackRemaining;
        var stage = GetStage(timeControl, GetMovesCompleted(checkpoint, checkpoint.ActiveColor));
        TimeSpan delayRemaining = elapsed < stage.Delay ? stage.Delay - elapsed : TimeSpan.Zero;
        TimeSpan mainElapsed = elapsed > stage.Delay ? elapsed - stage.Delay : TimeSpan.Zero;

        if (checkpoint.ActiveColor == 'w')
            whiteRemaining = ClampToZero(whiteRemaining - mainElapsed);
        else
            blackRemaining = ClampToZero(blackRemaining - mainElapsed);
        return new(
            whiteRemaining,
            blackRemaining,
            checkpoint.ActiveColor,
            delayRemaining,
            checkpoint.PausedAtUtc.HasValue,
            GetStageIndex(timeControl, GetMovesCompleted(checkpoint, checkpoint.ActiveColor)),
            now);
    }

    internal static PlayableMatchClockCheckpoint Pause(
        PlayableMatchClockCheckpoint checkpoint, DateTimeOffset now) =>
        checkpoint.PausedAtUtc.HasValue ? checkpoint : checkpoint with { PausedAtUtc = now };

    internal static PlayableMatchClockCheckpoint Resume(
        PlayableMatchClockCheckpoint checkpoint, DateTimeOffset now)
    {
        if (!checkpoint.PausedAtUtc.HasValue)
            return checkpoint;
        TimeSpan pausedDuration = now - checkpoint.PausedAtUtc.Value;
        return checkpoint with
        {
            TurnStartedAtUtc = checkpoint.TurnStartedAtUtc + pausedDuration,
            PausedAtUtc = null
        };
    }

    internal static PlayableMatchClockCheckpoint RestartTurn(
        PlayableMatchClockCheckpoint checkpoint, DateTimeOffset now) =>
        checkpoint with { TurnStartedAtUtc = now, PausedAtUtc = null };

    internal static PlayableMatchClockState SnapshotAtTurnStart(
        PlayableMatchTimeControl timeControl, PlayableMatchClockCheckpoint checkpoint)
    {
        var stage = GetStage(timeControl, GetMovesCompleted(checkpoint, checkpoint.ActiveColor));
        return new(
            checkpoint.WhiteRemaining,
            checkpoint.BlackRemaining,
            checkpoint.ActiveColor,
            stage.Delay,
            checkpoint.PausedAtUtc.HasValue,
            GetStageIndex(timeControl, GetMovesCompleted(checkpoint, checkpoint.ActiveColor)),
            checkpoint.TurnStartedAtUtc);
    }

    private static void ValidateRestore(
        PlayableMatchTimeControl timeControl, char expectedActiveColor,
        PlayableMatchClockRestore restore)
    {
        restore.Validate();
        if (restore.ActiveColor != expectedActiveColor)
            throw new ArgumentException(
                $"Restored active color '{restore.ActiveColor}' does not match loaded position active color '{expectedActiveColor}'.",
                nameof(restore));
        int activeMovesCompleted = restore.ActiveColor == 'w'
            ? restore.WhiteMovesCompleted
            : restore.BlackMovesCompleted;
        var stage = GetStage(timeControl, activeMovesCompleted);
        if (restore.DelayRemaining > stage.Delay)
            throw new ArgumentOutOfRangeException(nameof(restore), "Delay remaining cannot exceed the current stage delay.");
        int expectedStageIndex = GetStageIndex(timeControl, activeMovesCompleted);
        if (restore.ActiveStageIndex != expectedStageIndex)
            throw new ArgumentOutOfRangeException(
                nameof(restore),
                $"Active stage index '{restore.ActiveStageIndex}' does not match the restored move counts. Expected '{expectedStageIndex}'.");
    }

    private static (int White, int Black) ResolveCompletedMoveCounts(Fen currentFen)
    {
        int blackMoves = Math.Max(0, currentFen.FullmoveNumber - 1);
        return (currentFen.ActiveColor == 'b' ? blackMoves + 1 : blackMoves, blackMoves);
    }

    private static TimeSpan ComputeElapsed(PlayableMatchClockCheckpoint checkpoint, DateTimeOffset now)
    {
        DateTimeOffset effectiveNow = checkpoint.PausedAtUtc ?? now;
        TimeSpan elapsed = effectiveNow - checkpoint.TurnStartedAtUtc;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private static (TimeSpan Remaining, int MovesCompleted) CompleteSide(
        PlayableMatchTimeControl timeControl, TimeSpan remaining,
        int movesCompleted, TimeSpan elapsed,
        TimeSpan increment)
    {
        remaining = ClampToZero(remaining - elapsed);
        if (remaining <= TimeSpan.Zero && timeControl.TimeoutPolicy != PlayableMatchTimeoutPolicy.Ignore)
            return (TimeSpan.Zero, movesCompleted);
        movesCompleted++;
        return (remaining + increment + GetAddedStageTime(timeControl, movesCompleted), movesCompleted);
    }

    private static (TimeSpan Increment, TimeSpan Delay) GetStage(
        PlayableMatchTimeControl timeControl, int movesCompleted)
    {
        int stageIndex = GetStageIndex(timeControl, movesCompleted);
        if (stageIndex == 0)
            return (timeControl.IncrementPerMove, timeControl.DelayPerMove);
        var stage = timeControl.AdditionalStages[stageIndex - 1];
        return (stage.IncrementPerMove, stage.DelayPerMove);
    }

    private static int GetStageIndex(PlayableMatchTimeControl? timeControl, int movesCompleted)
    {
        if (!timeControl.HasValue || timeControl.Value.AdditionalStages.IsDefaultOrEmpty)
            return 0;
        var index = 0;
        for (var i = 0; i < timeControl.Value.AdditionalStages.Length; i++)
        {
            if (movesCompleted >= timeControl.Value.AdditionalStages[i].TriggerMovesPerSide)
                index = i + 1;
        }

        return index;
    }

    private static TimeSpan GetAddedStageTime(PlayableMatchTimeControl timeControl, int movesCompleted)
    {
        foreach (var stage in timeControl.AdditionalStages)
        {
            if (movesCompleted == stage.TriggerMovesPerSide)
                return stage.AddedTime;
        }

        return TimeSpan.Zero;
    }

    private static int GetMovesCompleted(PlayableMatchClockCheckpoint checkpoint, char side) =>
        side == 'w' ? checkpoint.WhiteMovesCompleted : checkpoint.BlackMovesCompleted;

    private static TimeSpan ClampToZero(TimeSpan remaining) =>
        remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    private static char Opposite(char color) => color == 'w' ? 'b' : 'w';
}

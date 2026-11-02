using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.UCI.Protocol.Internal;

internal readonly record struct ValidatedPlayableMatchSave(
    Fen CurrentFen, PlayableMatchClockCheckpoint[] ClockHistory, PendingPromotionRequest? PendingPromotion);

/// <summary>Validates persisted state independently of either session's mutable runtime.</summary>
internal static class PlayableMatchSaveValidator
{
    internal static ValidatedPlayableMatchSave ValidateAndResolve(
        PlayableMatchSave save, PlayableMatchTimeControl? timeControl,
        PlayableMatchClaimableDrawPolicy claimPolicy, PlayableMatchDrawOfferPolicy offerPolicy,
        CancellationToken ct = default, DateTimeOffset? resumeAtUtc = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!TimeControlsEqual(timeControl, save.TimeControl))
            throw Invalid("The saved time control does not match this session.");
        if (save.ClaimableDrawPolicy != claimPolicy || save.DrawOfferPolicy != offerPolicy)
            throw Invalid("The saved draw policies do not match this session.");
        if (save.ClockHistory.IsDefault || save.ClockHistoryOriginMoveCount < 0 ||
            save.Setup.PlayedMoves.IsDefault || save.ClockHistoryOriginMoveCount > save.Setup.PlayedMoves.Length)
            throw Invalid("The saved clock history is not initialized or has an invalid origin.");
        int expectedClocks = timeControl.HasValue ? save.Setup.PlayedMoves.Length - save.ClockHistoryOriginMoveCount + 1 : 0;
        if (save.ClockHistory.Length != expectedClocks || !timeControl.HasValue && save.ClockHistoryOriginMoveCount != 0 ||
            timeControl.HasValue != save.Setup.Clock.HasValue || timeControl.HasValue && !save.Setup.Clock!.Value.ExactRestore.HasValue)
            throw Invalid("A timed save requires one turn-start clock per retained position and an exact current live clock.");
        if (!Enum.IsDefined(typeof(PlayableMatchResultReason), save.Result.Reason) ||
            save.Result.Winner.HasValue && save.Result.Winner is not ('w' or 'b') ||
            !save.Result.IsTerminal && save.Result.Winner.HasValue ||
            save.Result.Winner.HasValue && save.Result.Reason is not (PlayableMatchResultReason.Checkmate or
                PlayableMatchResultReason.Resignation or PlayableMatchResultReason.Timeout) ||
            save.DrawOfferedBy.HasValue && save.DrawOfferedBy is not ('w' or 'b') ||
            save.Result.IsTerminal && (save.DrawOfferedBy.HasValue || save.PendingPromotionMove is not null) ||
            save.DeclaredDrawMove is not null && save.Result.Reason is not
                (PlayableMatchResultReason.FiftyMoveRule or PlayableMatchResultReason.ThreefoldRepetition))
            throw Invalid("The save has inconsistent result, draw-offer or promotion metadata.");

        var repetitions = new Dictionary<string, int>(StringComparer.Ordinal);
        var currentFen = PlayableMatchHistoryValidator.ValidateAndResolve(save.Setup, claimPolicy, ct, repetitions);
        var checkpoints = expectedClocks == 0 ? Array.Empty<PlayableMatchClockCheckpoint>() : new PlayableMatchClockCheckpoint[expectedClocks];
        PlayableMatchClockState? currentClock = null;
        if (timeControl.HasValue)
        {
            for (int index = 0; index < checkpoints.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var restore = save.ClockHistory[index];
                int moveIndex = index + save.ClockHistoryOriginMoveCount;
                char activeColor = (moveIndex & 1) == 0 ? save.Setup.BaseFen.ActiveColor : LocalPositionRules.Opposite(save.Setup.BaseFen.ActiveColor);
                var checkpoint = PlayableMatchClockKernel.Restore(timeControl.Value, activeColor, restore);
                if (PlayableMatchClockKernel.SnapshotAtTurnStart(timeControl.Value, checkpoint).DelayRemaining != restore.DelayRemaining)
                    throw Invalid("Historical clock entries must represent the start of their turns.");
                if (index > 0)
                {
                    var previous = checkpoints[index - 1];
                    if (restore.WhiteMovesCompleted != checked(previous.WhiteMovesCompleted + (previous.ActiveColor == 'w' ? 1 : 0)) ||
                        restore.BlackMovesCompleted != checked(previous.BlackMovesCompleted + (previous.ActiveColor == 'b' ? 1 : 0)))
                        throw Invalid("Saved clock move counts must advance once per played move.");
                }
                checkpoints[index] = checkpoint;
            }
            var live = save.Setup.Clock!.Value.ExactRestore!.Value;
            // Validate against the original timestamp before rebasing a resumed match.
            PlayableMatchClockKernel.RestoreWithBaseline(timeControl.Value, currentFen.ActiveColor, save.ClockHistory[^1], live);
            currentClock = new(live.WhiteRemaining, live.BlackRemaining, live.ActiveColor, live.DelayRemaining,
                live.IsPaused, live.ActiveStageIndex, live.SnapshotUtc);
            if (save.Result.IsTerminal && !live.IsPaused)
                throw Invalid("A finished match must have a paused clock.");
            if (resumeAtUtc.HasValue)
                live = live with { SnapshotUtc = resumeAtUtc.Value };
            checkpoints[^1] = PlayableMatchClockKernel.RestoreWithBaseline(timeControl.Value, currentFen.ActiveColor,
                save.ClockHistory[^1], live);
        }

        ct.ThrowIfCancellationRequested();
        var legalMoves = currentFen.GetLegalMoves();
        int occurrences = repetitions[LocalPositionRules.BuildRepetitionKey(currentFen)];
        var natural = PlayableMatchAdjudication.Evaluate(currentFen, legalMoves.Length, currentClock, timeControl, occurrences, claimPolicy);
        ValidateResult(save, currentFen, legalMoves, repetitions, occurrences, natural.Result);
        PendingPromotionRequest? promotion = null;
        if (save.PendingPromotionMove is not null)
        {
            if (!LocalPositionRules.TryCreatePendingPromotion(currentFen, save.PendingPromotionMove, legalMoves, out var request))
                throw Invalid("The saved promotion is not legal in the saved position.");
            promotion = request;
        }
        return new(currentFen, checkpoints, promotion);
    }

    private static void ValidateResult(PlayableMatchSave save, Fen fen,
        System.Collections.Immutable.ImmutableArray<string> legalMoves,
        Dictionary<string, int> repetitions, int occurrences, PlayableMatchResult natural)
    {
        if (natural.IsTerminal)
        {
            if (save.Result != natural || save.DeclaredDrawMove is not null)
                throw Invalid("The saved result and winner do not match the board, history and clock.");
            return;
        }
        if (!save.Result.IsTerminal)
            return;
        switch (save.Result.Reason)
        {
            case PlayableMatchResultReason.DrawAgreement:
                return;
            case PlayableMatchResultReason.Resignation:
                if (save.Result.Winner.HasValue
                    ? LocalPositionRules.CanPossiblyMate(fen, save.Result.Winner.Value)
                    : !LocalPositionRules.CanPossiblyMate(fen, 'w') || !LocalPositionRules.CanPossiblyMate(fen, 'b'))
                    return;
                break;
            case PlayableMatchResultReason.FiftyMoveRule:
            case PlayableMatchResultReason.ThreefoldRepetition:
                if (save.DeclaredDrawMove is null)
                {
                    if (save.Result.Reason == PlayableMatchResultReason.FiftyMoveRule ? fen.HalfmoveClock >= 100 : occurrences >= 3)
                        return;
                    break;
                }
                string move = LocalPositionRules.NormalizeMove(save.DeclaredDrawMove);
                if (!legalMoves.ContainsUciMove(move))
                    break;
                var next = fen.ApplyMove(move);
                if (save.Result.Reason == PlayableMatchResultReason.FiftyMoveRule
                    ? next.HalfmoveClock >= 100
                    : repetitions.TryGetValue(LocalPositionRules.BuildRepetitionKey(next), out int previous) && previous >= 2)
                    return;
                break;
        }
        throw Invalid("The saved result is not supported by its board, history, clock or declared draw move.");
    }

    private static bool TimeControlsEqual(PlayableMatchTimeControl? first, PlayableMatchTimeControl? second)
    {
        if (!first.HasValue || !second.HasValue)
            return first.HasValue == second.HasValue;
        var a = first.Value;
        var b = second.Value;
        return a.InitialTime == b.InitialTime && a.IncrementPerMove == b.IncrementPerMove && a.DelayPerMove == b.DelayPerMove &&
            a.TimeoutPolicy == b.TimeoutPolicy && a.AdditionalStages.SequenceEqual(b.AdditionalStages);
    }

    private static ArgumentException Invalid(string message) => new(message, "save");
}

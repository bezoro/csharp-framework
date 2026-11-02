using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.UCI.Protocol.Internal;

/// <summary>Pure result adjudication shared by live sessions and persisted-state validation.</summary>
internal static class PlayableMatchAdjudication
{
    internal static (PlayableMatchResult Result, PlayableMatchResult? ClaimableResult) Evaluate(
        Fen fen, int legalMoveCount, PlayableMatchClockState? clock,
        PlayableMatchTimeControl? timeControl, int repetitions, PlayableMatchClaimableDrawPolicy claimPolicy)
    {
        if (legalMoveCount == 0)
            return LocalPositionRules.IsCurrentPlayerInCheck(fen)
                ? (new(PlayableMatchResultReason.Checkmate, LocalPositionRules.Opposite(fen.ActiveColor)), null)
                : (new(PlayableMatchResultReason.Stalemate, null), null);
        if (clock.HasValue && timeControl?.TimeoutPolicy == PlayableMatchTimeoutPolicy.AutomaticLoss &&
            (fen.ActiveColor == 'w' ? clock.Value.WhiteRemaining : clock.Value.BlackRemaining) <= TimeSpan.Zero)
        {
            char opponent = LocalPositionRules.Opposite(fen.ActiveColor);
            return (new(PlayableMatchResultReason.Timeout, LocalPositionRules.CanPossiblyMate(fen, opponent) ? opponent : null), null);
        }
        if (fen.HalfmoveClock >= 150)
            return (new(PlayableMatchResultReason.SeventyFiveMoveRule, null), null);
        if (LocalPositionRules.HasInsufficientMaterial(fen))
            return (new(PlayableMatchResultReason.InsufficientMaterial, null), null);
        if (repetitions >= 5)
            return (new(PlayableMatchResultReason.FivefoldRepetition, null), null);
        var reason = fen.HalfmoveClock >= 100 ? PlayableMatchResultReason.FiftyMoveRule
            : repetitions >= 3 ? PlayableMatchResultReason.ThreefoldRepetition : PlayableMatchResultReason.None;
        if (reason == PlayableMatchResultReason.None)
            return (default, null);
        var draw = new PlayableMatchResult(reason, null);
        return claimPolicy == PlayableMatchClaimableDrawPolicy.Automatic ? (draw, null) : (default, draw);
    }
}

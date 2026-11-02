using System.Collections.Immutable;

namespace Bezoro.Chess.UCI.Protocol.API.Types;

/// <summary>
///     An immutable local save containing the move history, retained clock history, promotion, draw offer and result.
///     Engines and analysis caches are deliberately excluded. Serialize this value using the application's storage format.
/// </summary>
/// <param name="Setup">Initial position, complete move list, and exact current live clock for a timed match.</param>
/// <param name="TimeControl">Clock configuration required by the receiving session, or null for an untimed match.</param>
/// <param name="ClockHistory">Turn-start clock snapshots for each retained position, including the current turn's original baseline.</param>
/// <param name="ClockHistoryOriginMoveCount">Move-list index represented by the first clock snapshot.</param>
/// <param name="Result">Adjudicated result, including resignation, agreement or a claimed draw.</param>
/// <param name="DrawOfferedBy">Side with a pending draw offer, if any.</param>
/// <param name="PendingPromotionMove">Uncompleted promotion move prefix, if any.</param>
/// <param name="ClaimableDrawPolicy">Draw-claim policy required by the receiving session.</param>
/// <param name="DrawOfferPolicy">Draw-offer lifetime policy required by the receiving session.</param>
/// <param name="DeclaredDrawMove">Legal intended move declared when claiming a draw without executing that move.</param>
public readonly record struct PlayableMatchSave(
    PlayableMatchSetup Setup,
    PlayableMatchTimeControl? TimeControl,
    ImmutableArray<PlayableMatchClockRestore> ClockHistory,
    int ClockHistoryOriginMoveCount = 0,
    PlayableMatchResult Result = default,
    char? DrawOfferedBy = null,
    string? PendingPromotionMove = null,
    PlayableMatchClaimableDrawPolicy ClaimableDrawPolicy = PlayableMatchClaimableDrawPolicy.Automatic,
    PlayableMatchDrawOfferPolicy DrawOfferPolicy = PlayableMatchDrawOfferPolicy.ExpireOnMove,
    string? DeclaredDrawMove = null);

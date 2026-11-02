namespace Bezoro.Chess.Networking;

/// <summary>Actions a player may request from the authoritative match owner.</summary>
public enum ChessMatchCommandKind
{
    /// <summary>A complete coordinate move, including its promotion suffix when needed.</summary>
    Move,
    /// <summary>Resign as the authenticated player, including outside their turn.</summary>
    Resign,
    /// <summary>Offer a draw as the authenticated player.</summary>
    OfferDraw,
    /// <summary>Accept an opponent's pending draw offer.</summary>
    AcceptDraw,
    /// <summary>Decline an opponent's pending draw offer.</summary>
    DeclineDraw,
    /// <summary>Claim an available draw on the authenticated player's turn, optionally declaring the intended move.</summary>
    ClaimDraw,
    /// <summary>Propose undoing the requested number of halfmoves, if enabled by the host.</summary>
    RequestTakeback,
    /// <summary>Accept the opponent's takeback proposal.</summary>
    AcceptTakeback,
    /// <summary>Decline the opponent's takeback proposal.</summary>
    DeclineTakeback
}

/// <summary>An immutable player request, containing no client-authored position, clock, or result.</summary>
/// <param name="MatchId">The host-assigned match identity.</param>
/// <param name="ExpectedRevision">The latest host revision observed by the player.</param>
/// <param name="Sequence">Positive per-seat command sequence. Increment after acceptance.</param>
/// <param name="Kind">Requested action.</param>
/// <param name="Move">Lowercase UCI move, or an optional legal intended move when claiming a draw.</param>
/// <param name="TakebackHalfmoves">Halfmoves to restore for a takeback proposal.</param>
public readonly record struct ChessMatchCommand(
    Guid MatchId,
    long ExpectedRevision,
    long Sequence,
    ChessMatchCommandKind Kind,
    string? Move = null,
    int TakebackHalfmoves = 1);

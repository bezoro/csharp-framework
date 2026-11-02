using System.Collections.Immutable;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.Networking;

/// <summary>An opponent-consented request to return to a previous position.</summary>
/// <param name="OfferedBy">Authenticated player proposing the takeback.</param>
/// <param name="Halfmoves">Number of halfmoves to undo.</param>
public readonly record struct ChessTakebackOffer(ChessPlayerSeat OfferedBy, int Halfmoves);

/// <summary>An immutable server snapshot that either board presentation can consume.</summary>
/// <param name="MatchId">Host-assigned match identity.</param>
/// <param name="Revision">Monotonic revision of accepted actions and newly adjudicated results.</param>
/// <param name="BaseFen">Original position before the complete move history in <paramref name="State"/>.</param>
/// <param name="State">Authoritative rules, clocks, history, and result.</param>
/// <param name="TakebackOffer">Current optional takeback proposal.</param>
/// <remarks>Clock snapshots advance without changing the revision. Animate clocks locally from their timestamp; only the host adjudicates timeout.</remarks>
public readonly record struct ChessMatchSnapshot(
    Guid MatchId,
    long Revision,
    Fen BaseFen,
    PlayableMatchState State,
    ChessTakebackOffer? TakebackOffer)
{
    /// <summary>Copies the UCI history into an immutable request for an explicit engine move or game review.</summary>
    public ChessEnginePosition CreateEnginePosition()
    {
        if (State.MoveHistory.IsDefault)
            throw new InvalidOperationException("An initialized match snapshot is required.");
        var moves = ImmutableArray.CreateBuilder<string>(State.MoveHistory.Length);
        foreach (var move in State.MoveHistory) moves.Add(move.Move);
        return new(MatchId, Revision, BaseFen, moves.MoveToImmutable());
    }
}

/// <summary>The result of validating a player command.</summary>
public enum ChessMatchCommandStatus
{
    /// <summary>No command has been evaluated; the default receipt is not an acceptance.</summary>
    Uninitialized,
    /// <summary>The action was committed. Replaying the last identical accepted command returns its original receipt.</summary>
    Accepted,
    /// <summary>The command belongs to a different match.</summary>
    WrongMatch,
    /// <summary>The player must refresh their authoritative snapshot.</summary>
    StaleRevision,
    /// <summary>The sequence was older than, or conflicted with, the last accepted command from this seat.</summary>
    InvalidSequence,
    /// <summary>This seat cannot perform this action now.</summary>
    NotAuthorized,
    /// <summary>The action or move is invalid in the current match.</summary>
    InvalidAction,
    /// <summary>The match is already terminal.</summary>
    MatchFinished,
    /// <summary>The host's explicit move-history budget was reached.</summary>
    MoveLimitReached
}

/// <summary>A command acknowledgement with the snapshot needed to continue or resynchronize.</summary>
/// <param name="Status">Validation outcome.</param>
/// <param name="Sequence">The submitted sequence.</param>
/// <param name="Snapshot">State after acceptance, or the current state when rejected.</param>
public readonly record struct ChessMatchCommandReceipt(
    ChessMatchCommandStatus Status,
    long Sequence,
    ChessMatchSnapshot Snapshot);

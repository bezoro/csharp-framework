using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.PlayerProfiles;

namespace Bezoro.Chess.Progression;

/// <summary>Distinguishes actual training from imported or replayed games.</summary>
public enum ChessMatchProgressSource
{
    /// <summary>A finalized match owned by the local live-training application.</summary>
    LiveTraining,
    /// <summary>A replay; it cannot award training Elo, history, or quest progress.</summary>
    Replay
}

/// <summary>A trusted finalized training result, supplied by the session owner rather than a view or a replay controller.</summary>
/// <remarks>Keep MatchId unchanged across undo, retries, and repeated terminal notifications. This is a local application boundary, not server authentication.</remarks>
public readonly struct ChessTrainingMatch : IEquatable<ChessTrainingMatch>
{
    /// <summary>Validates a terminal result and the player's side. Opponent ratings refer only to the local training pool.</summary>
    public ChessTrainingMatch(string matchId, string opponentId, double opponentRating, char playerSide,
        PlayableMatchResult result, DateTimeOffset completedAt, ChessMatchProgressSource source, string? replayReference = null)
    {
        ProgressText.Require(matchId, nameof(matchId));
        ProgressText.Require(opponentId, nameof(opponentId));
        if (replayReference is not null) ProgressText.Require(replayReference, nameof(replayReference));
        _ = new EloRating(opponentRating);
        if (playerSide is not ('w' or 'b')) throw new ArgumentOutOfRangeException(nameof(playerSide));
        if (source is not (ChessMatchProgressSource.LiveTraining or ChessMatchProgressSource.Replay)) throw new ArgumentOutOfRangeException(nameof(source));
        if (result.Reason < PlayableMatchResultReason.Checkmate || result.Reason > PlayableMatchResultReason.SeventyFiveMoveRule
            || result.Winner is not (null or 'w' or 'b')) throw new ArgumentException("A valid terminal result is required.", nameof(result));
        bool checkmate = result.Reason == PlayableMatchResultReason.Checkmate;
        bool mayBeDecisive = checkmate || result.Reason is PlayableMatchResultReason.Resignation or PlayableMatchResultReason.Timeout;
        if (checkmate && result.Winner is null || !mayBeDecisive && result.Winner is not null)
            throw new ArgumentException("The winner must agree with the terminal reason.", nameof(result));
        MatchId = matchId; OpponentId = opponentId; OpponentRating = opponentRating; PlayerSide = playerSide;
        Result = result; CompletedAt = completedAt; Source = source; ReplayReference = replayReference;
    }

    /// <summary>Gets the stable logical match identity.</summary>
    public string MatchId { get; }
    /// <summary>Gets the application-owned opponent identity, including engine identities.</summary>
    public string OpponentId { get; }
    /// <summary>Gets the opponent's pre-match local training rating.</summary>
    public double OpponentRating { get; }
    /// <summary>Gets the player's white or black side.</summary>
    public char PlayerSide { get; }
    /// <summary>Gets the finalized rule-session result.</summary>
    public PlayableMatchResult Result { get; }
    /// <summary>Gets the completion time supplied by the session owner.</summary>
    public DateTimeOffset CompletedAt { get; }
    /// <summary>Gets whether this is live training or a non-awarding replay.</summary>
    public ChessMatchProgressSource Source { get; }
    /// <summary>Gets an optional host-owned replay reference.</summary>
    public string? ReplayReference { get; }
    /// <summary>Gets whether the constructor initialized this value.</summary>
    public bool IsValid => MatchId is not null;
    /// <summary>Gets the outcome derived from the terminal result and the player's side.</summary>
    public PlayerMatchOutcome Outcome => Result.Winner is null ? PlayerMatchOutcome.Draw : Result.Winner == PlayerSide ? PlayerMatchOutcome.Win : PlayerMatchOutcome.Loss;
    /// <inheritdoc />
    public bool Equals(ChessTrainingMatch other) => MatchId == other.MatchId && OpponentId == other.OpponentId
        && OpponentRating.Equals(other.OpponentRating) && PlayerSide == other.PlayerSide && Result == other.Result
        && CompletedAt.Equals(other.CompletedAt) && Source == other.Source && ReplayReference == other.ReplayReference;
    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ChessTrainingMatch other && Equals(other);
    /// <inheritdoc />
    public override int GetHashCode() => MatchId is null ? 0 : StringComparer.Ordinal.GetHashCode(MatchId);
}

internal static class ProgressText
{
    internal static void Require(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            throw new ArgumentException("A nonblank value of at most 256 characters is required.", parameter);
    }
}

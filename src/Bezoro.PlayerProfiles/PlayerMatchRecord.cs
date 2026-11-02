namespace Bezoro.PlayerProfiles;

/// <summary>Describes a completed match from one player's perspective.</summary>
public enum PlayerMatchOutcome
{
    /// <summary>The player lost.</summary>
    Loss,
    /// <summary>The match was drawn.</summary>
    Draw,
    /// <summary>The player won.</summary>
    Win
}

/// <summary>Immutable metadata for a completed match; replay content is stored separately by the host.</summary>
public readonly struct PlayerMatchRecord : IEquatable<PlayerMatchRecord>
{
    /// <summary>Creates a completed match record. Identities and game-mode identifiers are case-sensitive.</summary>
    public PlayerMatchRecord(string matchId, string playerId, string opponentId, PlayerMatchOutcome outcome,
        DateTimeOffset completedAt, string gameMode, bool isRated = false, string? replayReference = null)
    {
        Identifier.Require(matchId, nameof(matchId));
        Identifier.Require(playerId, nameof(playerId));
        Identifier.Require(opponentId, nameof(opponentId));
        Identifier.Require(gameMode, nameof(gameMode));
        if (playerId == opponentId) throw new ArgumentException("A player cannot be their own opponent.", nameof(opponentId));
        if (outcome < PlayerMatchOutcome.Loss || outcome > PlayerMatchOutcome.Win) throw new ArgumentOutOfRangeException(nameof(outcome));
        if (replayReference is not null) Identifier.Require(replayReference, nameof(replayReference));
        MatchId = matchId;
        PlayerId = playerId;
        OpponentId = opponentId;
        Outcome = outcome;
        CompletedAt = completedAt;
        GameMode = gameMode;
        IsRated = isRated;
        ReplayReference = replayReference;
    }

    /// <summary>Gets the unique application match identifier; null for the default value.</summary>
    public string MatchId { get; }
    /// <summary>Gets the player whose history owns this record.</summary>
    public string PlayerId { get; }
    /// <summary>Gets the opponent identity; engines can have application-defined identities.</summary>
    public string OpponentId { get; }
    /// <summary>Gets the result from this player's perspective.</summary>
    public PlayerMatchOutcome Outcome { get; }
    /// <summary>Gets the authoritative completion timestamp.</summary>
    public DateTimeOffset CompletedAt { get; }
    /// <summary>Gets the application's mode or rating-pool identifier.</summary>
    public string GameMode { get; }
    /// <summary>Gets whether the authoritative host designated this match as rated.</summary>
    public bool IsRated { get; }
    /// <summary>Gets an optional opaque replay-storage reference.</summary>
    public string? ReplayReference { get; }
    /// <summary>Gets whether this value was initialized through its constructor.</summary>
    public bool IsValid => MatchId is not null;
    /// <inheritdoc />
    public bool Equals(PlayerMatchRecord other) => MatchId == other.MatchId && PlayerId == other.PlayerId &&
        OpponentId == other.OpponentId && Outcome == other.Outcome && CompletedAt.Equals(other.CompletedAt) &&
        GameMode == other.GameMode && IsRated == other.IsRated && ReplayReference == other.ReplayReference;
    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PlayerMatchRecord other && Equals(other);
    /// <inheritdoc />
    public override int GetHashCode() => MatchId is null ? 0 : StringComparer.Ordinal.GetHashCode(MatchId);
    /// <summary>Compares complete match metadata.</summary>
    public static bool operator ==(PlayerMatchRecord left, PlayerMatchRecord right) => left.Equals(right);
    /// <summary>Compares complete match metadata.</summary>
    public static bool operator !=(PlayerMatchRecord left, PlayerMatchRecord right) => !left.Equals(right);
}

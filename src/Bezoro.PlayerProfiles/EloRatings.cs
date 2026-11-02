namespace Bezoro.PlayerProfiles;

/// <summary>A finite Elo rating and the number of results applied to it.</summary>
public readonly struct EloRating
{
    /// <summary>Creates a rating. The host chooses initial ratings and rating pools.</summary>
    public EloRating(double value, int gamesPlayed = 0)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (gamesPlayed < 0) throw new ArgumentOutOfRangeException(nameof(gamesPlayed));
        Value = value;
        GamesPlayed = gamesPlayed;
    }

    /// <summary>Gets the finite rating. Negative ratings are mathematically supported.</summary>
    public double Value { get; }
    /// <summary>Gets the number of applied results.</summary>
    public int GamesPlayed { get; }
}

/// <summary>The immutable result of updating two ratings from the same pre-match pair.</summary>
public readonly struct EloRatingUpdate
{
    internal EloRatingUpdate(EloRating firstPlayer, EloRating secondPlayer)
    {
        FirstPlayer = firstPlayer;
        SecondPlayer = secondPlayer;
    }

    /// <summary>Gets the first player's new rating.</summary>
    public EloRating FirstPlayer { get; }
    /// <summary>Gets the second player's new rating.</summary>
    public EloRating SecondPlayer { get; }
}

/// <summary>Pure, allocation-free Elo calculations with a 400-point logistic scale.</summary>
/// <remarks>This is configurable Elo, not Chess.com's Glicko system or the complete FIDE rating regulations.</remarks>
public static class EloRatings
{
    /// <summary>Calculates the first player's expected score in [0, 1].</summary>
    public static double ExpectedScore(EloRating firstPlayer, EloRating secondPlayer)
    {
        // Divide before subtracting so extreme finite ratings cannot overflow the difference.
        double exponent = secondPlayer.Value / 400 - firstPlayer.Value / 400;
        if (exponent >= 308) return 0;
        if (exponent <= -308) return 1;
        return 1 / (1 + Math.Pow(10, exponent));
    }

    /// <summary>Updates both players atomically as values. K factors must be finite and positive; defaults are 32.</summary>
    /// <remarks>Apply a completed authoritative match exactly once; this pure function retains no match identifiers.</remarks>
    public static EloRatingUpdate ApplyResult(EloRating firstPlayer, EloRating secondPlayer, PlayerMatchOutcome firstPlayerOutcome,
        double firstPlayerKFactor = 32, double secondPlayerKFactor = 32)
    {
        ValidateKFactor(firstPlayerKFactor, nameof(firstPlayerKFactor));
        ValidateKFactor(secondPlayerKFactor, nameof(secondPlayerKFactor));
        if (firstPlayerOutcome < PlayerMatchOutcome.Loss || firstPlayerOutcome > PlayerMatchOutcome.Win)
            throw new ArgumentOutOfRangeException(nameof(firstPlayerOutcome));
        if (firstPlayer.GamesPlayed == int.MaxValue || secondPlayer.GamesPlayed == int.MaxValue)
            throw new InvalidOperationException("The applied-result counter is exhausted.");
        double score = firstPlayerOutcome == PlayerMatchOutcome.Win ? 1 : firstPlayerOutcome == PlayerMatchOutcome.Draw ? 0.5 : 0;
        double difference = score - ExpectedScore(firstPlayer, secondPlayer);
        double first = firstPlayer.Value + firstPlayerKFactor * difference;
        double second = secondPlayer.Value - secondPlayerKFactor * difference;
        if (double.IsInfinity(first) || double.IsInfinity(second)) throw new OverflowException("The updated rating is not finite.");
        return new EloRatingUpdate(new EloRating(first, firstPlayer.GamesPlayed + 1), new EloRating(second, secondPlayer.GamesPlayed + 1));
    }

    private static void ValidateKFactor(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) throw new ArgumentOutOfRangeException(name);
    }
}

namespace Bezoro.Chess.UCI.Protocol.API.Types;

/// <summary>
///     Engine estimates of wins, draws, and losses per thousand games, relative to the side to move.
/// </summary>
public readonly record struct UciWdl
{
	/// <summary>Creates a distribution whose nonnegative values must sum to 1000.</summary>
	/// <param name="wins">Estimated wins per thousand games.</param>
	/// <param name="draws">Estimated draws per thousand games.</param>
	/// <param name="losses">Estimated losses per thousand games.</param>
	public UciWdl(int wins, int draws, int losses)
	{
		if ((uint)wins > 1000) throw new ArgumentOutOfRangeException(nameof(wins));
		if ((uint)draws > 1000) throw new ArgumentOutOfRangeException(nameof(draws));
		if ((uint)losses > 1000) throw new ArgumentOutOfRangeException(nameof(losses));
		if (wins + draws + losses != 1000)
			throw new ArgumentException("Win, draw, and loss estimates must sum to 1000.");
		Wins = wins;
		Draws = draws;
		Losses = losses;
	}

	/// <summary>Gets the estimated wins per thousand games.</summary>
	public int Wins { get; }

	/// <summary>Gets the estimated draws per thousand games.</summary>
	public int Draws { get; }

	/// <summary>Gets the estimated losses per thousand games.</summary>
	public int Losses { get; }

	/// <summary>Identifies a valid distribution, excluding the uninitialized default value.</summary>
	public bool IsValid => Wins + Draws + Losses == 1000;

	/// <summary>Gets expected points on a zero-to-one scale, counting a draw as half a point.</summary>
	/// <exception cref="InvalidOperationException">The value is uninitialized.</exception>
	public double ExpectedPoints => IsValid
		? (Wins + 0.5 * Draws) / 1000.0
		: throw new InvalidOperationException("An uninitialized WDL value has no expected points.");

	/// <summary>Gets the same distribution from the opponent's perspective.</summary>
	public UciWdl OpponentPerspective => new(Losses, Draws, Wins);

	/// <summary>Formats the three UCI values without recursively formatting the opponent-perspective property.</summary>
	public override string ToString() => $"{Wins} {Draws} {Losses}";
}

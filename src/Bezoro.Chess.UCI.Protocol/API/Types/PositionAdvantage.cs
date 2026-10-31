using System.Globalization;
using Bezoro.Chess.UCI.Protocol.API.Common.Helpers;

namespace Bezoro.Chess.UCI.Protocol.API.Types;

/// <summary>
///     Represents a normalized position advantage summary for display and UI rendering.
/// </summary>
/// <param name="Normalized">Normalized advantage in the inclusive range [-1, 1].</param>
/// <param name="Summary">Human-readable summary from the player's perspective.</param>
/// <param name="Score">Underlying player-relative score.</param>
public readonly record struct PositionAdvantage(double Normalized, string Summary, PositionScore Score)
{
	/// <summary>
	///     Creates a player-relative advantage summary from a normalized player-relative score.
	/// </summary>
	/// <param name="score">Player-relative score.</param>
	/// <returns>A normalized advantage summary.</returns>
	public static PositionAdvantage FromScore(PositionScore score)
	{
		if (score.Mate is int adjustedMate)
		{
			long plyToMate = Math.Abs((long)adjustedMate);
			double magnitude = plyToMate switch
			{
				<= 1 => 1.0,
				2    => 0.90,
				3    => 0.82,
				4    => 0.74,
				_    => 0.66
			};

			double normalizedMate = adjustedMate > 0 ? magnitude : -magnitude;
			string mateSummary = adjustedMate > 0
				? $"Advantage +{normalizedMate.ToString("0.00", CultureInfo.InvariantCulture)} | You mate in {plyToMate}"
				: $"Advantage {normalizedMate.ToString("0.00", CultureInfo.InvariantCulture)} | Engine mate in {plyToMate}";

			return new(normalizedMate, mateSummary, score);
		}

		int adjustedCp = score.Cp ?? 0;
		double normalized = AdvantageScale.NormalizeCp(adjustedCp);

		string cpText = adjustedCp.ToString(CultureInfo.InvariantCulture);
		long magnitudeCp = Math.Abs((long)adjustedCp);
		string magnitudeText = (magnitudeCp / 100.0).ToString("0.0", CultureInfo.InvariantCulture);

		string summary = normalized switch
		{
			> 0 => $"Advantage +{normalized.ToString("0.00", CultureInfo.InvariantCulture)} | You +{magnitudeText} pawns ({cpText} cp)",
			< 0 => $"Advantage {normalized.ToString("0.00", CultureInfo.InvariantCulture)} | Engine +{magnitudeText} pawns ({magnitudeCp.ToString(CultureInfo.InvariantCulture)} cp)",
			_   => "Advantage 0.00 | Even (0 cp)"
		};

		return new(normalized, summary, score);
	}

	/// <summary>
	///     Creates a player-relative advantage summary from raw engine output.
	/// </summary>
	/// <param name="rawCpScore">Engine centipawn score, from side-to-move perspective.</param>
	/// <param name="rawMateScore">Engine mate score, from side-to-move perspective.</param>
	/// <param name="sideToMove">Side to move for the evaluated position: <c>w</c> or <c>b</c>.</param>
	/// <param name="playerColor">Player side: <c>w</c> or <c>b</c>.</param>
	/// <returns>A normalized advantage summary.</returns>
	public static PositionAdvantage FromEngineScore(
		int? rawCpScore,
		int? rawMateScore,
		char sideToMove,
		char playerColor)
	{
		var score = PositionScore.FromEngineScore(
			rawCpScore,
			rawMateScore,
			sideToMove,
			playerColor
		);
		return FromScore(score);
	}

	/// <summary>
	///     Returns a neutral advantage representing an in-progress analysis.
	/// </summary>
	public static PositionAdvantage Pending() => new(0, "Analyzing...", new(0, null));

	/// <summary>
	///     Returns a neutral advantage representing game over.
	/// </summary>
	public static PositionAdvantage GameOver() => new(0, "Game over", new(0, null));

	/// <summary>
	///     Returns the neutral starting-game advantage.
	/// </summary>
	public static PositionAdvantage GameStart() => new(0, "Advantage 0.00 | Even (0 cp)", new(0, null));
}

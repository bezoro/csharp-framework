namespace Bezoro.Chess.UCI.Protocol.API.Common.Helpers;

/// <summary>
///     Converts centipawn scores into a bounded normalized range suitable for simple UI indicators.
/// </summary>
public static class AdvantageScale
{
	private const double MAX_NORMALIZED_CP = 0.95;
	private const double CP_SCALE          = 1_000.0;

	/// <summary>
	///     Maps a centipawn score into the inclusive range [-0.95, 0.95].
	/// </summary>
	/// <param name="cp">Centipawn score.</param>
	/// <returns>Normalized score preserving sign symmetry.</returns>
	public static double NormalizeCp(int cp)
	{
		if (cp == 0)
			return 0;

		double magnitude = Math.Tanh(Math.Abs((long)cp) / CP_SCALE) * MAX_NORMALIZED_CP;
		return cp > 0 ? magnitude : -magnitude;
	}
}

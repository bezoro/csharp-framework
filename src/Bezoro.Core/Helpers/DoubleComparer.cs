using System.Runtime.CompilerServices;

namespace Bezoro.Core.Helpers;

/// <summary>
///     Helper class for robust double-precision floating-point number comparisons.
///     Handles precision issues inherent in floating-point arithmetic.
///     <para>
///         Usage guidance:
///         - Use AreEqual for small numbers or when absolute difference matters
///         - Use AreEqualRelative for large numbers or when relative difference matters
///         - Use AreEqualRobust when dealing with unknown magnitude ranges (recommended default)
///     </para>
/// </summary>
public static class DoubleComparer
{
	/// <summary>
	///     Default absolute epsilon for double comparisons.
	/// </summary>
	public const double DEFAULT_DOUBLE_EPSILON = 1e-15;

	/// <summary>
	///     Default relative epsilon for double relative comparisons.
	/// </summary>
	public const double DEFAULT_DOUBLE_RELATIVE_EPSILON = 1e-14;

	/// <summary>
	///     ULP (Unit in the Last Place) epsilon for double: approximately double.Epsilon * 2^52.
	///     Provides a cushion for boundary rounding errors in relative comparisons.
	/// </summary>
	private const double DOUBLE_ULP_EPSILON = 2.2204460492503131e-16;

	/// <summary>
	///     Determines if two double values are equal using absolute epsilon comparison.
	/// </summary>
	/// <param name="a">First double value</param>
	/// <param name="b">Second double value</param>
	/// <param name="epsilon">Absolute epsilon tolerance (default: 1e-15). Must be non-negative.</param>
	/// <returns>True if the values are considered equal within the epsilon tolerance</returns>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when epsilon is negative</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool AreEqual(double a, double b, double epsilon = DEFAULT_DOUBLE_EPSILON)
	{
		if (epsilon < 0 || double.IsNaN(epsilon) || double.IsInfinity(epsilon)) throw new ArgumentOutOfRangeException(nameof(epsilon), "Epsilon must be non-negative");

		// ReSharper disable once CompareOfFloatsByEqualityOperator
		if (a == b) return true; // Fast path for exact matches and equal infinities (intentional)
		if (double.IsNaN(a) || double.IsNaN(b)) return false;
		if (double.IsInfinity(a) || double.IsInfinity(b)) return false;

		return Math.Abs(a - b) <= epsilon;
	}

	/// <summary>
	///     Determines if two double values are equal using relative epsilon comparison.
	///     Better for comparing larger numbers. Falls back to absolute comparison for near-zero values.
	/// </summary>
	/// <param name="a">First double value</param>
	/// <param name="b">Second double value</param>
	/// <param name="absoluteEpsilon">Absolute epsilon fallback for near-zero values (default: 1e-15). Must be non-negative.</param>
	/// <param name="relativeEpsilon">Relative epsilon tolerance (default: 1e-14). Must be non-negative.</param>
	/// <returns>True if the values are considered equal within the relative tolerance</returns>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when epsilon is negative</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool AreEqualRelative(
		double a,
		double b,
		double absoluteEpsilon = DEFAULT_DOUBLE_EPSILON,
		double relativeEpsilon = DEFAULT_DOUBLE_RELATIVE_EPSILON)
	{
		if (absoluteEpsilon < 0 || double.IsNaN(absoluteEpsilon) || double.IsInfinity(absoluteEpsilon))
			throw new ArgumentOutOfRangeException(nameof(absoluteEpsilon), "Epsilon must be non-negative");

		if (relativeEpsilon < 0 || double.IsNaN(relativeEpsilon) || double.IsInfinity(relativeEpsilon))
			throw new ArgumentOutOfRangeException(nameof(relativeEpsilon), "Epsilon must be non-negative");

		// ReSharper disable once CompareOfFloatsByEqualityOperator
		if (a == b) return true; // Fast path for exact matches and equal infinities (intentional)
		if (double.IsNaN(a) || double.IsNaN(b)) return false;
		if (double.IsInfinity(a) || double.IsInfinity(b)) return false;

		double diff   = Math.Abs(a - b);
		double absA   = Math.Abs(a);
		double absB   = Math.Abs(b);
		double maxAbs = absA > absB ? absA : absB;

		// Use absolute epsilon for near-zero values
		if (maxAbs <= absoluteEpsilon) return diff <= absoluteEpsilon;

		// Opposite finite extremes can overflow their difference and threshold.
		// Normalize before subtracting in that case; the relative distance is at most two.
		if (double.IsInfinity(diff))
			return Math.Abs(a / maxAbs - b / maxAbs) <= relativeEpsilon + DOUBLE_ULP_EPSILON;

		// Use relative epsilon with ULP cushion for larger numbers
		double threshold = relativeEpsilon * maxAbs;
		return diff <= threshold + DOUBLE_ULP_EPSILON * maxAbs;
	}

	/// <summary>
	///     Robust comparison using both absolute and relative epsilon.
	///     Uses absolute epsilon for numbers close to zero, relative epsilon for larger numbers.
	/// </summary>
	/// <param name="a">First double value</param>
	/// <param name="b">Second double value</param>
	/// <param name="absoluteEpsilon">Absolute epsilon tolerance (default: 1e-15). Must be non-negative.</param>
	/// <param name="relativeEpsilon">Relative epsilon tolerance (default: 1e-14). Must be non-negative.</param>
	/// <returns>True if the values are considered equal</returns>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when epsilon is negative</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool AreEqualRobust(
		double a,
		double b,
		double absoluteEpsilon = DEFAULT_DOUBLE_EPSILON,
		double relativeEpsilon = DEFAULT_DOUBLE_RELATIVE_EPSILON)
	{
		if (absoluteEpsilon < 0 || double.IsNaN(absoluteEpsilon) || double.IsInfinity(absoluteEpsilon))
			throw new ArgumentOutOfRangeException(nameof(absoluteEpsilon), "Epsilon must be non-negative");

		if (relativeEpsilon < 0 || double.IsNaN(relativeEpsilon) || double.IsInfinity(relativeEpsilon))
			throw new ArgumentOutOfRangeException(nameof(relativeEpsilon), "Epsilon must be non-negative");

		// ReSharper disable once CompareOfFloatsByEqualityOperator
		if (a == b) return true; // Fast path for exact matches and equal infinities (intentional)
		if (double.IsNaN(a) || double.IsNaN(b)) return false;
		if (double.IsInfinity(a) || double.IsInfinity(b)) return false;

		double diff   = Math.Abs(a - b);
		double absA   = Math.Abs(a);
		double absB   = Math.Abs(b);
		double maxAbs = absA > absB ? absA : absB;

		// Use absolute epsilon for numbers close to zero
		if (maxAbs <= absoluteEpsilon) return diff <= absoluteEpsilon;

		// Opposite finite extremes can overflow their difference and threshold.
		// Normalize before subtracting in that case; the relative distance is at most two.
		if (double.IsInfinity(diff))
			return Math.Abs(a / maxAbs - b / maxAbs) <= relativeEpsilon + DOUBLE_ULP_EPSILON;

		// Use relative epsilon with ULP cushion for larger numbers
		return diff <= (relativeEpsilon + DOUBLE_ULP_EPSILON) * maxAbs;
	}

	/// <summary>
	///     Determines if the first double is greater than the second using robust comparison.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsGreaterThan(
		double a,
		double b,
		double absoluteEpsilon = DEFAULT_DOUBLE_EPSILON,
		double relativeEpsilon = DEFAULT_DOUBLE_RELATIVE_EPSILON) =>
		!AreEqualRobust(a, b, absoluteEpsilon, relativeEpsilon) && a > b;

	/// <summary>
	///     Determines if the first double is greater than or equal to the second using robust comparison.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsGreaterThanOrEqual(
		double a,
		double b,
		double absoluteEpsilon = DEFAULT_DOUBLE_EPSILON,
		double relativeEpsilon = DEFAULT_DOUBLE_RELATIVE_EPSILON) =>
		AreEqualRobust(a, b, absoluteEpsilon, relativeEpsilon) || a > b;

	/// <summary>
	///     Determines if the first double is less than the second using robust comparison.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsLessThan(
		double a,
		double b,
		double absoluteEpsilon = DEFAULT_DOUBLE_EPSILON,
		double relativeEpsilon = DEFAULT_DOUBLE_RELATIVE_EPSILON) =>
		!AreEqualRobust(a, b, absoluteEpsilon, relativeEpsilon) && a < b;

	/// <summary>
	///     Determines if the first double is less than or equal to the second using robust comparison.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsLessThanOrEqual(
		double a,
		double b,
		double absoluteEpsilon = DEFAULT_DOUBLE_EPSILON,
		double relativeEpsilon = DEFAULT_DOUBLE_RELATIVE_EPSILON) =>
		AreEqualRobust(a, b, absoluteEpsilon, relativeEpsilon) || a < b;

	/// <summary>
	///     Determines if a double value is effectively zero.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsZero(double value, double epsilon = DEFAULT_DOUBLE_EPSILON)
	{
		if (epsilon < 0 || double.IsNaN(epsilon) || double.IsInfinity(epsilon))
			throw new ArgumentOutOfRangeException(nameof(epsilon), "Epsilon must be finite and nonnegative.");
		return Math.Abs(value) <= epsilon;
	}

	/// <summary>
	///     Returns the sign of a double value, treating values close to zero as zero.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static int Sign(double value, double epsilon = DEFAULT_DOUBLE_EPSILON) =>
		IsZero(value, epsilon) ? 0 : Math.Sign(value);
}

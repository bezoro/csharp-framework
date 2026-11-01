using System.Runtime.CompilerServices;

namespace Bezoro.Core.Helpers;

/// <summary>
///     Helper class for robust single-precision floating-point number comparisons.
///     Handles precision issues inherent in floating-point arithmetic.
///     <para>
///         Usage guidance:
///         - Use AreEqual for small numbers or when absolute difference matters
///         - Use AreEqualRelative for large numbers or when relative difference matters
///         - Use AreEqualRobust when dealing with unknown magnitude ranges (recommended default)
///     </para>
/// </summary>
public static class FloatComparer
{
	/// <summary>
	///     Default absolute epsilon for float comparisons.
	/// </summary>
	public const float DEFAULT_FLOAT_EPSILON = 1e-6f;

	/// <summary>
	///     Default relative epsilon for float relative comparisons.
	/// </summary>
	public const float DEFAULT_FLOAT_RELATIVE_EPSILON = 1e-5f;

	/// <summary>
	///     ULP (Unit in the Last Place) epsilon for float: approximately float.Epsilon * 2^23.
	///     Provides a cushion for boundary rounding errors in relative comparisons.
	/// </summary>
	private const float FLOAT_ULP_EPSILON = 1.1920929e-07f;

	/// <summary>
	///     Determines if two float values are equal using absolute epsilon comparison.
	/// </summary>
	/// <param name="a">First float value</param>
	/// <param name="b">Second float value</param>
	/// <param name="epsilon">Absolute epsilon tolerance (default: 1e-6f). Must be non-negative.</param>
	/// <returns>True if the values are considered equal within the epsilon tolerance</returns>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when epsilon is negative</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool AreEqual(float a, float b, float epsilon = DEFAULT_FLOAT_EPSILON)
	{
		if (epsilon < 0 || float.IsNaN(epsilon) || float.IsInfinity(epsilon)) throw new ArgumentOutOfRangeException(nameof(epsilon), "Epsilon must be non-negative");

		// ReSharper disable once CompareOfFloatsByEqualityOperator
		if (a == b) return true; // Fast path for exact matches and equal infinities (intentional)
		if (float.IsNaN(a) || float.IsNaN(b)) return false;
		if (float.IsInfinity(a) || float.IsInfinity(b)) return false;

		return MathF.Abs(a - b) <= epsilon;
	}

	/// <summary>
	///     Determines if two float values are equal using relative epsilon comparison.
	///     Better for comparing larger numbers. Falls back to absolute comparison for near-zero values.
	/// </summary>
	/// <param name="a">First float value</param>
	/// <param name="b">Second float value</param>
	/// <param name="absoluteEpsilon">Absolute epsilon fallback for near-zero values (default: 1e-6f). Must be non-negative.</param>
	/// <param name="relativeEpsilon">Relative epsilon tolerance (default: 1e-5f). Must be non-negative.</param>
	/// <returns>True if the values are considered equal within the relative tolerance</returns>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when epsilon is negative</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool AreEqualRelative(
		float a,
		float b,
		float absoluteEpsilon = DEFAULT_FLOAT_EPSILON,
		float relativeEpsilon = DEFAULT_FLOAT_RELATIVE_EPSILON)
	{
		if (absoluteEpsilon < 0 || float.IsNaN(absoluteEpsilon) || float.IsInfinity(absoluteEpsilon))
			throw new ArgumentOutOfRangeException(nameof(absoluteEpsilon), "Epsilon must be non-negative");

		if (relativeEpsilon < 0 || float.IsNaN(relativeEpsilon) || float.IsInfinity(relativeEpsilon))
			throw new ArgumentOutOfRangeException(nameof(relativeEpsilon), "Epsilon must be non-negative");

		// ReSharper disable once CompareOfFloatsByEqualityOperator
		if (a == b) return true; // Fast path for exact matches and equal infinities (intentional)
		if (float.IsNaN(a) || float.IsNaN(b)) return false;
		if (float.IsInfinity(a) || float.IsInfinity(b)) return false;

		float diff   = MathF.Abs(a - b);
		float absA   = MathF.Abs(a);
		float absB   = MathF.Abs(b);
		float maxAbs = absA > absB ? absA : absB;

		// Use absolute epsilon for near-zero values
		if (maxAbs <= absoluteEpsilon) return diff <= absoluteEpsilon;

		// Opposite finite extremes can overflow their difference and threshold.
		// Normalize before subtracting in that case; the relative distance is at most two.
		if (float.IsInfinity(diff))
			return MathF.Abs(a / maxAbs - b / maxAbs) <= relativeEpsilon + FLOAT_ULP_EPSILON;

		// Use relative epsilon with ULP cushion for larger numbers
		float threshold = relativeEpsilon * maxAbs;
		return diff <= threshold + FLOAT_ULP_EPSILON * maxAbs;
	}

	/// <summary>
	///     Robust comparison using both absolute and relative epsilon.
	///     Uses absolute epsilon for numbers close to zero, relative epsilon for larger numbers.
	/// </summary>
	/// <param name="a">First float value</param>
	/// <param name="b">Second float value</param>
	/// <param name="absoluteEpsilon">Absolute epsilon tolerance (default: 1e-6f). Must be non-negative.</param>
	/// <param name="relativeEpsilon">Relative epsilon tolerance (default: 1e-5f). Must be non-negative.</param>
	/// <returns>True if the values are considered equal</returns>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when epsilon is negative</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool AreEqualRobust(
		float a,
		float b,
		float absoluteEpsilon = DEFAULT_FLOAT_EPSILON,
		float relativeEpsilon = DEFAULT_FLOAT_RELATIVE_EPSILON)
	{
		if (absoluteEpsilon < 0 || float.IsNaN(absoluteEpsilon) || float.IsInfinity(absoluteEpsilon))
			throw new ArgumentOutOfRangeException(nameof(absoluteEpsilon), "Epsilon must be non-negative");

		if (relativeEpsilon < 0 || float.IsNaN(relativeEpsilon) || float.IsInfinity(relativeEpsilon))
			throw new ArgumentOutOfRangeException(nameof(relativeEpsilon), "Epsilon must be non-negative");

		// ReSharper disable once CompareOfFloatsByEqualityOperator
		if (a == b) return true; // Fast path for exact matches and equal infinities (intentional)
		if (float.IsNaN(a) || float.IsNaN(b)) return false;
		if (float.IsInfinity(a) || float.IsInfinity(b)) return false;

		float diff   = MathF.Abs(a - b);
		float absA   = MathF.Abs(a);
		float absB   = MathF.Abs(b);
		float maxAbs = absA > absB ? absA : absB;

		// Use absolute epsilon for numbers close to zero
		if (maxAbs <= absoluteEpsilon) return diff <= absoluteEpsilon;

		// Opposite finite extremes can overflow their difference and threshold.
		// Normalize before subtracting in that case; the relative distance is at most two.
		if (float.IsInfinity(diff))
			return MathF.Abs(a / maxAbs - b / maxAbs) <= relativeEpsilon + FLOAT_ULP_EPSILON;

		// Use relative epsilon with ULP cushion for larger numbers
		return diff <= (relativeEpsilon + FLOAT_ULP_EPSILON) * maxAbs;
	}

	/// <summary>
	///     Determines if the first float is greater than the second using robust comparison.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsGreaterThan(
		float a,
		float b,
		float absoluteEpsilon = DEFAULT_FLOAT_EPSILON,
		float relativeEpsilon = DEFAULT_FLOAT_RELATIVE_EPSILON) =>
		!AreEqualRobust(a, b, absoluteEpsilon, relativeEpsilon) && a > b;

	/// <summary>
	///     Determines if the first float is greater than or equal to the second using robust comparison.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsGreaterThanOrEqual(
		float a,
		float b,
		float absoluteEpsilon = DEFAULT_FLOAT_EPSILON,
		float relativeEpsilon = DEFAULT_FLOAT_RELATIVE_EPSILON) =>
		AreEqualRobust(a, b, absoluteEpsilon, relativeEpsilon) || a > b;

	/// <summary>
	///     Determines if the first float is less than the second using robust comparison.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsLessThan(
		float a,
		float b,
		float absoluteEpsilon = DEFAULT_FLOAT_EPSILON,
		float relativeEpsilon = DEFAULT_FLOAT_RELATIVE_EPSILON) =>
		!AreEqualRobust(a, b, absoluteEpsilon, relativeEpsilon) && a < b;

	/// <summary>
	///     Determines if the first float is less than or equal to the second using robust comparison.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsLessThanOrEqual(
		float a,
		float b,
		float absoluteEpsilon = DEFAULT_FLOAT_EPSILON,
		float relativeEpsilon = DEFAULT_FLOAT_RELATIVE_EPSILON) =>
		AreEqualRobust(a, b, absoluteEpsilon, relativeEpsilon) || a < b;

	/// <summary>
	///     Determines if a float value is effectively zero.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsZero(float value, float epsilon = DEFAULT_FLOAT_EPSILON)
	{
		if (epsilon < 0 || float.IsNaN(epsilon) || float.IsInfinity(epsilon))
			throw new ArgumentOutOfRangeException(nameof(epsilon), "Epsilon must be finite and nonnegative.");
		return MathF.Abs(value) <= epsilon;
	}

	/// <summary>
	///     Returns the sign of a float value, treating values close to zero as zero.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static int Sign(float value, float epsilon = DEFAULT_FLOAT_EPSILON) =>
		IsZero(value, epsilon) ? 0 : Math.Sign(value);
}

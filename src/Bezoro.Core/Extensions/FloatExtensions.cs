using System.Runtime.CompilerServices;
using Bezoro.Core.Helpers;
using Bezoro.Core.Types.Exceptions;

namespace Bezoro.Core.Extensions;

/// <summary>
///     Provides extension methods for <see cref="float" /> values including bounds checking,
///     mapping, and approximate floating-point comparisons via <see cref="FloatComparer" />.
/// </summary>
public static class FloatExtensions
{
	/// <summary>
	///     Determines whether this value is approximately equal to <paramref name="other" /> using robust comparison.
	/// </summary>
	/// <param name="value">The value to compare.</param>
	/// <param name="other">The value to compare against.</param>
	/// <param name="absoluteEpsilon">Absolute epsilon tolerance. Must be non-negative.</param>
	/// <param name="relativeEpsilon">Relative epsilon tolerance. Must be non-negative.</param>
	/// <returns><c>true</c> if the values are considered equal within tolerance; otherwise, <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool ApproxEquals(
		this float value,
		float other,
		float absoluteEpsilon = FloatComparer.DEFAULT_FLOAT_EPSILON,
		float relativeEpsilon = FloatComparer.DEFAULT_FLOAT_RELATIVE_EPSILON) =>
		FloatComparer.AreEqualRobust(value, other, absoluteEpsilon, relativeEpsilon);

	/// <summary>
	///     Determines whether this value is approximately zero.
	/// </summary>
	/// <param name="value">The value to check.</param>
	/// <param name="epsilon">Absolute epsilon tolerance. Must be non-negative.</param>
	/// <returns><c>true</c> if the value is effectively zero within tolerance; otherwise, <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsApproxZero(this float value, float epsilon = FloatComparer.DEFAULT_FLOAT_EPSILON) =>
		FloatComparer.IsZero(value, epsilon);

	/// <summary>
	///     Determines whether this value is approximately greater than <paramref name="other" />.
	/// </summary>
	/// <param name="value">The value to compare.</param>
	/// <param name="other">The value to compare against.</param>
	/// <param name="absoluteEpsilon">Absolute epsilon tolerance. Must be non-negative.</param>
	/// <param name="relativeEpsilon">Relative epsilon tolerance. Must be non-negative.</param>
	/// <returns><c>true</c> if this value is strictly greater than <paramref name="other" /> beyond tolerance.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsApproxGreaterThan(
		this float value,
		float other,
		float absoluteEpsilon = FloatComparer.DEFAULT_FLOAT_EPSILON,
		float relativeEpsilon = FloatComparer.DEFAULT_FLOAT_RELATIVE_EPSILON) =>
		FloatComparer.IsGreaterThan(value, other, absoluteEpsilon, relativeEpsilon);

	/// <summary>
	///     Determines whether this value is approximately greater than or equal to <paramref name="other" />.
	/// </summary>
	/// <param name="value">The value to compare.</param>
	/// <param name="other">The value to compare against.</param>
	/// <param name="absoluteEpsilon">Absolute epsilon tolerance. Must be non-negative.</param>
	/// <param name="relativeEpsilon">Relative epsilon tolerance. Must be non-negative.</param>
	/// <returns><c>true</c> if this value is greater than or approximately equal to <paramref name="other" />.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsApproxGreaterThanOrEqual(
		this float value,
		float other,
		float absoluteEpsilon = FloatComparer.DEFAULT_FLOAT_EPSILON,
		float relativeEpsilon = FloatComparer.DEFAULT_FLOAT_RELATIVE_EPSILON) =>
		FloatComparer.IsGreaterThanOrEqual(value, other, absoluteEpsilon, relativeEpsilon);

	/// <summary>
	///     Determines whether this value is approximately less than <paramref name="other" />.
	/// </summary>
	/// <param name="value">The value to compare.</param>
	/// <param name="other">The value to compare against.</param>
	/// <param name="absoluteEpsilon">Absolute epsilon tolerance. Must be non-negative.</param>
	/// <param name="relativeEpsilon">Relative epsilon tolerance. Must be non-negative.</param>
	/// <returns><c>true</c> if this value is strictly less than <paramref name="other" /> beyond tolerance.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsApproxLessThan(
		this float value,
		float other,
		float absoluteEpsilon = FloatComparer.DEFAULT_FLOAT_EPSILON,
		float relativeEpsilon = FloatComparer.DEFAULT_FLOAT_RELATIVE_EPSILON) =>
		FloatComparer.IsLessThan(value, other, absoluteEpsilon, relativeEpsilon);

	/// <summary>
	///     Determines whether this value is approximately less than or equal to <paramref name="other" />.
	/// </summary>
	/// <param name="value">The value to compare.</param>
	/// <param name="other">The value to compare against.</param>
	/// <param name="absoluteEpsilon">Absolute epsilon tolerance. Must be non-negative.</param>
	/// <param name="relativeEpsilon">Relative epsilon tolerance. Must be non-negative.</param>
	/// <returns><c>true</c> if this value is less than or approximately equal to <paramref name="other" />.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsApproxLessThanOrEqual(
		this float value,
		float other,
		float absoluteEpsilon = FloatComparer.DEFAULT_FLOAT_EPSILON,
		float relativeEpsilon = FloatComparer.DEFAULT_FLOAT_RELATIVE_EPSILON) =>
		FloatComparer.IsLessThanOrEqual(value, other, absoluteEpsilon, relativeEpsilon);

	/// <summary>
	///     Returns the sign of this value, treating values approximately zero as zero.
	/// </summary>
	/// <param name="value">The value to evaluate.</param>
	/// <param name="epsilon">Absolute epsilon tolerance. Must be non-negative.</param>
	/// <returns>-1, 0, or 1 indicating the approximate sign of the value.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static int ApproxSign(this float value, float epsilon = FloatComparer.DEFAULT_FLOAT_EPSILON) =>
		FloatComparer.Sign(value, epsilon);

	/// <summary>
	///     Determines whether <paramref name="value" /> is within the inclusive range defined by <paramref name="min" /> and
	///     <paramref name="max" />.
	/// </summary>
	/// <param name="value">The value to check.</param>
	/// <param name="min">The minimum (inclusive) value.</param>
	/// <param name="max">The maximum (inclusive) value.</param>
	/// <returns>
	///     <c>true</c> if <paramref name="value" /> is greater than or equal to <paramref name="min" /> and less than or
	///     equal to <paramref name="max" />; otherwise, <c>false</c>.
	/// </returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsBetween(this float value, float min, float max) => value >= min && value <= max;

	/// <summary>
	///     Maps a value from one range to another, preserving proportional distance.
	/// </summary>
	/// <param name="value">The float value to map.</param>
	/// <param name="fromMin">The minimum bound of the source range.</param>
	/// <param name="fromMax">The maximum bound of the source range.</param>
	/// <param name="toMin">The minimum bound of the target range.</param>
	/// <param name="toMax">The maximum bound of the target range.</param>
	/// <returns>
	///     The corresponding value in [<paramref name="toMin" />, <paramref name="toMax" />] that maps linearly from
	///     <paramref name="value" /> in [<paramref name="fromMin" />, <paramref name="fromMax" />].
	/// </returns>
	/// <remarks>
	///     The output is not clamped. If <paramref name="value" /> is outside the source range,
	///     the result will be proportionally outside the target range.
	/// </remarks>
	/// <exception cref="ArgumentException">
	///     Thrown when <paramref name="fromMin" /> equals <paramref name="fromMax" /> (zero-width source range).
	/// </exception>
	public static float Map(this float value, float fromMin, float fromMax, float toMin, float toMax)
	{
		if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value));
		if (float.IsNaN(fromMin) || float.IsInfinity(fromMin)) throw new ArgumentOutOfRangeException(nameof(fromMin));
		if (float.IsNaN(fromMax) || float.IsInfinity(fromMax)) throw new ArgumentOutOfRangeException(nameof(fromMax));
		if (float.IsNaN(toMin) || float.IsInfinity(toMin)) throw new ArgumentOutOfRangeException(nameof(toMin));
		if (float.IsNaN(toMax) || float.IsInfinity(toMax)) throw new ArgumentOutOfRangeException(nameof(toMax));
		double range = (double)fromMax - fromMin;
		if (range == 0)
			throw new ArgumentException("Source range cannot be zero (fromMin equals fromMax).", nameof(fromMax));

		return (float)(((double)value - fromMin) / range * ((double)toMax - toMin) + toMin);
	}

	/// <summary>
	///     Ensures the floating point value is greater than the specified minimum.
	///     Throws if the value is less than or equal to <paramref name="min" />.
	/// </summary>
	/// <param name="f">The float value to test.</param>
	/// <param name="min">The minimum exclusive lower bound.</param>
	/// <returns>The float value if it is valid.</returns>
	/// <exception cref="ValueTooSmallException">
	///     Thrown if <paramref name="f" /> is less than or equal to
	///     <paramref name="min" />.
	/// </exception>
	public static float ThrowIfLessOrEqualThan(this float f, float min)
	{
		if (f <= min)
			throw new ValueTooSmallException(f, min);

		return f;
	}

	/// <summary>
	///     Ensures the floating point value is greater than or equal to the specified minimum.
	///     Throws if the value is less than <paramref name="min" />.
	/// </summary>
	/// <param name="f">The float value to test.</param>
	/// <param name="min">The minimum inclusive lower bound.</param>
	/// <returns>The float value if it is valid.</returns>
	/// <exception cref="ValueTooSmallException">Thrown if <paramref name="f" /> is less than <paramref name="min" />.</exception>
	public static float ThrowIfLessThan(this float f, float min)
	{
		if (f < min)
			throw new ValueTooSmallException(f, min);

		return f;
	}

	/// <summary>
	///     Ensures the floating point value is less than the specified maximum.
	///     Throws if the value is greater than or equal to <paramref name="max" />.
	/// </summary>
	/// <param name="f">The float value to test.</param>
	/// <param name="max">The maximum exclusive upper bound.</param>
	/// <returns>The float value if it is valid.</returns>
	/// <exception cref="ValueTooLargeException">
	///     Thrown if <paramref name="f" /> is greater than or equal to
	///     <paramref name="max" />.
	/// </exception>
	public static float ThrowIfOverOrEqualThan(this float f, float max)
	{
		if (f >= max)
			throw new ValueTooLargeException(f, max);

		return f;
	}

	/// <summary>
	///     Ensures the floating point value is less than or equal to the specified maximum.
	///     Throws if the value is greater than <paramref name="max" />.
	/// </summary>
	/// <param name="f">The float value to test.</param>
	/// <param name="max">The maximum inclusive upper bound.</param>
	/// <returns>The float value if it is valid.</returns>
	/// <exception cref="ValueTooLargeException">Thrown if <paramref name="f" /> is greater than <paramref name="max" />.</exception>
	public static float ThrowIfOverThan(this float f, float max)
	{
		if (f > max)
			throw new ValueTooLargeException(f, max);

		return f;
	}
}

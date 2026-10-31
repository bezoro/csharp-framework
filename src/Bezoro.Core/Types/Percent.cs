using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Bezoro.Core.Types;

/// <summary>
///     Represents a percentage value constrained between 0 and 100 (inclusive).
/// </summary>
[DebuggerDisplay("{Value}%")]
public readonly struct Percent : IEquatable<Percent>, IComparable<Percent>
#if NET6_0_OR_GREATER
	,
	ISpanFormattable
#endif
{
	/// <summary>
	///     Precomputed ratio values for 0-100% to avoid division at runtime.
	/// </summary>
	private static ReadOnlySpan<float> RatioLookup =>
	[
		0.00f, 0.01f, 0.02f, 0.03f, 0.04f, 0.05f, 0.06f, 0.07f, 0.08f, 0.09f,
		0.10f, 0.11f, 0.12f, 0.13f, 0.14f, 0.15f, 0.16f, 0.17f, 0.18f, 0.19f,
		0.20f, 0.21f, 0.22f, 0.23f, 0.24f, 0.25f, 0.26f, 0.27f, 0.28f, 0.29f,
		0.30f, 0.31f, 0.32f, 0.33f, 0.34f, 0.35f, 0.36f, 0.37f, 0.38f, 0.39f,
		0.40f, 0.41f, 0.42f, 0.43f, 0.44f, 0.45f, 0.46f, 0.47f, 0.48f, 0.49f,
		0.50f, 0.51f, 0.52f, 0.53f, 0.54f, 0.55f, 0.56f, 0.57f, 0.58f, 0.59f,
		0.60f, 0.61f, 0.62f, 0.63f, 0.64f, 0.65f, 0.66f, 0.67f, 0.68f, 0.69f,
		0.70f, 0.71f, 0.72f, 0.73f, 0.74f, 0.75f, 0.76f, 0.77f, 0.78f, 0.79f,
		0.80f, 0.81f, 0.82f, 0.83f, 0.84f, 0.85f, 0.86f, 0.87f, 0.88f, 0.89f,
		0.90f, 0.91f, 0.92f, 0.93f, 0.94f, 0.95f, 0.96f, 0.97f, 0.98f, 0.99f,
		1.00f
	];

	/// <summary>
	///     Represents 0%.
	/// </summary>
	public static readonly Percent Zero = new(0);

	/// <summary>
	///     Represents 25%.
	/// </summary>
	public static readonly Percent Quarter = new(25);

	/// <summary>
	///     Represents 50%.
	/// </summary>
	public static readonly Percent Half = new(50);

	/// <summary>
	///     Represents 75%.
	/// </summary>
	public static readonly Percent ThreeQuarters = new(75);

	/// <summary>
	///     Represents 90%.
	/// </summary>
	public static readonly Percent Ninety = new(90);

	/// <summary>
	///     Represents 100%.
	/// </summary>
	public static readonly Percent Full = new(100);

	/// <summary>
	///     Initializes a new instance of <see cref="Percent" /> with the specified value.
	/// </summary>
	/// <param name="value">The percentage value (0–100).</param>
	/// <exception cref="ArgumentOutOfRangeException">
	///     Thrown when <paramref name="value" /> is greater than 100.
	/// </exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Percent(byte value)
	{
		if (value > 100)
			ThrowArgumentOutOfRange(value);

		Value = value;
	}

	/// <summary>
	///     Initializes a new instance of <see cref="Percent" /> from a current and maximum value.
	/// </summary>
	/// <param name="current">The current value.</param>
	/// <param name="max">The maximum value.</param>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Percent(uint current, uint max)
	{
		Value = CalculateValue(current, max);
	}

	/// <summary>Combines two current/maximum pairs without allocating a temporary array.</summary>
	public static Percent FromTotals((uint current, uint max) first, (uint current, uint max) second) =>
		new(CalculateValue((ulong)first.current + second.current, (ulong)first.max + second.max));

	/// <summary>
	///     Creates a <see cref="Percent" /> from the sum of one or more current/max pairs.
	/// </summary>
	/// <param name="totals">The current/max pairs to sum.</param>
	/// <returns>The computed percentage value.</returns>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="totals" /> is <c>null</c>.</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static Percent FromTotals(params (uint current, uint max)[] totals)
	{
		if (totals is null)
			throw new ArgumentNullException(nameof(totals));

		ulong totalCurrent = 0;
		ulong totalMax     = 0;

		foreach ((uint current, uint max) in totals)
		{
			totalCurrent += current;
			totalMax     += max;
		}

		return new(CalculateValue(totalCurrent, totalMax));
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void ThrowArgumentOutOfRange(byte value) =>
		throw new ArgumentOutOfRangeException(nameof(value), value, "Percentage must be between 0 and 100.");

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static byte CalculateValue(ulong current, ulong max)
	{
		if (max == 0)
			return 0;

		if (current >= max)
			return 100;

		ulong numerator = current * 100;
		ulong rounded   = numerator + max / 2;
		var   value     = (uint)(rounded / max);
		if (value > 100)
			value = 100;

		return (byte)value;
	}

	/// <summary>
	///     Gets the percentage value (0–100).
	/// </summary>
	public byte Value { get; }

	/// <summary>
	///     Indicates whether two <see cref="Percent" /> values are equal.
	/// </summary>
	/// <param name="left">The first percent.</param>
	/// <param name="right">The second percent.</param>
	/// <returns><c>true</c> if the values are equal; otherwise <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool operator ==(Percent left, Percent right) => left.Value == right.Value;

	/// <summary>
	///     Explicitly casts a <see cref="byte" /> value to a <see cref="Percent" />.
	/// </summary>
	/// <param name="value">The byte value to convert (0–100).</param>
	/// <returns>A <see cref="Percent" /> instance representing the given value.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static explicit operator Percent(byte value) => new(value);

	/// <summary>
	///     Determines whether one <see cref="Percent" /> is greater than another.
	/// </summary>
	/// <param name="left">The first percent.</param>
	/// <param name="right">The second percent.</param>
	/// <returns><c>true</c> if <paramref name="left" /> is greater than <paramref name="right" />; otherwise, <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool operator >(Percent left, Percent right) => left.Value > right.Value;

	/// <summary>
	///     Determines whether one <see cref="Percent" /> is greater than or equal to another.
	/// </summary>
	/// <param name="left">The first percent.</param>
	/// <param name="right">The second percent.</param>
	/// <returns>
	///     <c>true</c> if <paramref name="left" /> is greater than or equal to <paramref name="right" />; otherwise,
	///     <c>false</c>.
	/// </returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool operator >=(Percent left, Percent right) => left.Value >= right.Value;

	/// <summary>
	///     Implicitly converts a <see cref="Percent" /> to a <see cref="byte" /> representing its value.
	/// </summary>
	/// <param name="percent">The <see cref="Percent" /> instance.</param>
	/// <returns>The <see cref="byte" /> percent value (0–100).</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static implicit operator byte(Percent percent) => percent.Value;

	/// <summary>
	///     Indicates whether two <see cref="Percent" /> values are not equal.
	/// </summary>
	/// <param name="left">The first percent.</param>
	/// <param name="right">The second percent.</param>
	/// <returns><c>true</c> if the values are not equal; otherwise <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool operator !=(Percent left, Percent right) => left.Value != right.Value;

	/// <summary>
	///     Determines whether one <see cref="Percent" /> is less than another.
	/// </summary>
	/// <param name="left">The first percent.</param>
	/// <param name="right">The second percent.</param>
	/// <returns><c>true</c> if <paramref name="left" /> is less than <paramref name="right" />; otherwise, <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool operator <(Percent left, Percent right) => left.Value < right.Value;

	/// <summary>
	///     Determines whether one <see cref="Percent" /> is less than or equal to another.
	/// </summary>
	/// <param name="left">The first percent.</param>
	/// <param name="right">The second percent.</param>
	/// <returns>
	///     <c>true</c> if <paramref name="left" /> is less than or equal to <paramref name="right" />; otherwise,
	///     <c>false</c>.
	/// </returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool operator <=(Percent left, Percent right) => left.Value <= right.Value;

	#region Equality

	/// <summary>
	///     Compares the current <see cref="Percent" /> to another <see cref="Percent" />.
	/// </summary>
	/// <param name="other">A <see cref="Percent" /> to compare with this instance.</param>
	/// <returns>
	///     A value that indicates the relative order of the objects being compared.
	///     Less than zero: This instance is less than <paramref name="other" />.
	///     Zero: This instance is equal to <paramref name="other" />.
	///     Greater than zero: This instance is greater than <paramref name="other" />.
	/// </returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int CompareTo(Percent other) => Value.CompareTo(other.Value);

	/// <summary>
	///     Indicates whether the current <see cref="Percent" /> is equal to another <see cref="Percent" />.
	/// </summary>
	/// <param name="other">A <see cref="Percent" /> to compare with this instance.</param>
	/// <returns><c>true</c> if equal; otherwise, <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool Equals(Percent other) => Value == other.Value;

	/// <summary>
	///     Determines whether the specified object is equal to the current <see cref="Percent" />.
	/// </summary>
	/// <param name="obj">The object to compare to.</param>
	/// <returns>
	///     <c>true</c> if <paramref name="obj" /> is a <see cref="Percent" /> with the same value; otherwise,
	///     <c>false</c>.
	/// </returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override bool Equals(object? obj) => obj is Percent other && Equals(other);

	/// <summary>
	///     Returns the hash code for this instance.
	/// </summary>
	/// <returns>A 32-bit signed integer hash code.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override int GetHashCode() => Value;

	#endregion

	/// <summary>
	///     Returns a string representation of this <see cref="Percent" /> in "%" notation (e.g., "42%").
	/// </summary>
	/// <returns>The percent value as a string with a trailing percent sign.</returns>
	public override string ToString() => $"{Value}%";

#if NET6_0_OR_GREATER
	/// <summary>
	///     Tries to format the value into the provided span of characters.
	/// </summary>
	/// <param name="destination">The span in which to write the formatted value.</param>
	/// <param name="charsWritten">When this method returns, contains the number of characters written.</param>
	/// <param name="format">A span containing the characters that represent a standard or custom format string (unused).</param>
	/// <param name="provider">An object that supplies culture-specific formatting information (unused).</param>
	/// <returns><c>true</c> if the formatting was successful; otherwise, <c>false</c>.</returns>
	public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
	{
		// Max length: "100%" = 4 characters
		if (!Value.TryFormat(destination, out charsWritten))
			return false;

		if (destination.Length <= charsWritten)
		{
			charsWritten = 0;
			return false;
		}

		destination[charsWritten++] = '%';
		return true;
	}

	/// <summary>
	///     Formats the value using the specified format and format provider.
	/// </summary>
	/// <param name="format">The format to use (unused).</param>
	/// <param name="formatProvider">The provider to use to format the value (unused).</param>
	/// <returns>The formatted string representation.</returns>
	public string ToString(string? format, IFormatProvider? formatProvider) => ToString();
#endif

	/// <summary>
	///     Converts the percentage to a decimal ratio (0.0–1.0).
	/// </summary>
	/// <returns>The percent value as a float in the range 0.0 to 1.0.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public float ToRatio() => RatioLookup[Value];
}

using System.Runtime.InteropServices;

namespace Bezoro.Core.Helpers;

/// <summary>
///     Provides methods for converting between different array shapes and representations.
/// </summary>
public static class ArrayConverter
{
	/// <summary>
	///     Reshapes a one-dimensional array into a two-dimensional array in row-major order.
	/// </summary>
	/// <typeparam name="T">The element type of the arrays.</typeparam>
	/// <param name="from">The source one-dimensional array to reshape.</param>
	/// <param name="rows">The number of rows in the resulting array.</param>
	/// <param name="columns">The number of columns in the resulting array.</param>
	/// <returns>
	///     A new two-dimensional array containing the elements of the source arranged into the specified shape.
	/// </returns>
	/// <exception cref="ArgumentNullException">
	///     Thrown if <paramref name="from" /> is <c>null</c>.
	/// </exception>
	/// <exception cref="ArgumentOutOfRangeException">
	///     Thrown if <paramref name="rows" /> or <paramref name="columns" /> is negative.
	/// </exception>
	/// <exception cref="ArgumentException">
	///     Thrown if the length of <paramref name="from" /> does not equal
	///     <paramref name="rows" /> * <paramref name="columns" />.
	/// </exception>
	public static T[,] Reshape<T>(T[] from, int rows, int columns)
	{
#if NET6_0_OR_GREATER
		ArgumentNullException.ThrowIfNull(from);
#else
		if (from == null) throw new ArgumentNullException(nameof(from));
#endif

		if (rows < 0) throw new ArgumentOutOfRangeException(nameof(rows));
		if (columns < 0) throw new ArgumentOutOfRangeException(nameof(columns));

		long area = (long)rows * columns;
		if (area > int.MaxValue)
			throw new ArgumentOutOfRangeException(nameof(columns), "The requested area must fit in an Int32.");
		if (from.Length != area)
			throw new ArgumentException(
				$"Source length {from.Length} does not match the requested shape {rows}x{columns}.",
				nameof(from)
			);

		var result = new T[rows, columns];
		if (from.Length == 0) return result;

		from.AsSpan().CopyTo(MemoryMarshal.CreateSpan(ref result[0, 0], from.Length));
		return result;
	}

	/// <summary>
	///     Flattens a two-dimensional array to a one-dimensional array in row-major order.
	/// </summary>
	/// <typeparam name="T">The element type of the arrays.</typeparam>
	/// <param name="from">The source two-dimensional array to flatten.</param>
	/// <returns>
	///     A new one-dimensional array containing the elements of the source in row-major order.
	/// </returns>
	/// <exception cref="ArgumentNullException">
	///     Thrown if <paramref name="from" /> is <c>null</c>.
	/// </exception>
	public static T[] Flatten<T>(T[,] from)
	{
#if NET6_0_OR_GREATER
		ArgumentNullException.ThrowIfNull(from);
#else
		if (from == null) throw new ArgumentNullException(nameof(from));
#endif

		int length = from.Length;
		if (length == 0) return Array.Empty<T>();

		var result = new T[length];
		MemoryMarshal.CreateReadOnlySpan(ref from[0, 0], length).CopyTo(result);
		return result;
	}
}

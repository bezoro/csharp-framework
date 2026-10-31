using System.Runtime.CompilerServices;

namespace Bezoro.Core.Types;

/// <summary>
///     A stack-allocated, read-only view over two-dimensional grid data.
///     Provides high-performance read access without heap allocations.
/// </summary>
/// <typeparam name="T">The type of elements in the grid.</typeparam>
public readonly ref struct ReadOnlyGridSpan2D<T>
{
	/// <summary>Gets the height (number of rows) of the grid.</summary>
	public readonly int Height;

	/// <summary>Gets the width (number of columns) of the grid.</summary>
	public readonly int Width;

	private readonly ReadOnlySpan<T> _data;

	/// <summary>
	///     Initializes a new instance of the <see cref="ReadOnlyGridSpan2D{T}" /> struct.
	/// </summary>
	/// <param name="data">The span of data to wrap.</param>
	/// <param name="width">The width (number of columns) of the grid.</param>
	/// <param name="height">The height (number of rows) of the grid.</param>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public ReadOnlyGridSpan2D(ReadOnlySpan<T> data, int width, int height)
	{
		if (width <= 0)
			throw new ArgumentOutOfRangeException(nameof(width));
		if (height <= 0)
			throw new ArgumentOutOfRangeException(nameof(height));

		int length = checked(width * height);
		if (data.Length < length)
			throw new ArgumentException("The backing span is smaller than the grid.", nameof(data));

		_data  = data[..length];
		Width  = width;
		Height = height;
	}

	/// <summary>Gets the total number of elements in the grid.</summary>
	public int Length
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => Width * Height;
	}

	/// <summary>Gets the element at the specified coordinates.</summary>
	/// <param name="x">The column index (zero-based).</param>
	/// <param name="y">The row index (zero-based).</param>
	/// <returns>The element at the given coordinates.</returns>
	public ref readonly T this[int x, int y]
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			if (!IsInBounds(x, y))
				throw new IndexOutOfRangeException("Grid index out of range.");

			return ref _data[y * Width + x];
		}
	}

	/// <summary>Checks if the specified coordinates are within bounds.</summary>
	/// <param name="x">The column index.</param>
	/// <param name="y">The row index.</param>
	/// <returns><c>true</c> if the coordinates are valid; otherwise, <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool IsInBounds(int x, int y) => unchecked((uint)x) < (uint)Width && unchecked((uint)y) < (uint)Height;

	/// <summary>Gets a <see cref="ReadOnlySpan{T}" /> over the entire grid data.</summary>
	/// <returns>A read-only span over all grid elements.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public ReadOnlySpan<T> AsSpan() => _data[..(Width * Height)];

	/// <summary>Gets a <see cref="ReadOnlySpan{T}" /> over a single row.</summary>
	/// <param name="y">The row index (zero-based).</param>
	/// <returns>A read-only span over the specified row.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public ReadOnlySpan<T> GetRow(int y)
	{
		if (unchecked((uint)y) >= (uint)Height)
			throw new ArgumentOutOfRangeException(nameof(y));

		return _data.Slice(y * Width, Width);
	}
}

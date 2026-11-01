using System.Buffers;
using System.Runtime.CompilerServices;

namespace Bezoro.Core.Types;

/// <summary>
///     Represents a two-dimensional grid of elements of type <typeparamref name="T" />.
///     Uses a flat array for optimal performance and supports array pooling to minimize allocations.
/// </summary>
/// <typeparam name="T">The type of elements stored in the grid.</typeparam>
[Serializable]
public sealed class Grid2D<T> : IDisposable
{
	/// <summary>
	///     Backing storage for the grid elements (flat array for performance).
	/// </summary>
	private T[]? _data;

	/// <summary>
	///     Whether this instance owns the array (from pool) and should return it on dispose.
	/// </summary>
	private readonly bool _fromPool;

	/// <summary>
	///     Initializes a new instance of the <see cref="Grid2D{T}" /> class with the specified dimensions.
	/// </summary>
	/// <param name="width">The width (number of columns) of the grid. Must be greater than zero.</param>
	/// <param name="height">The height (number of rows) of the grid. Must be greater than zero.</param>
	/// <param name="usePooling">Whether to rent the backing array from <see cref="ArrayPool{T}" />.</param>
	/// <exception cref="ArgumentException">
	///     Thrown if <paramref name="width" /> or <paramref name="height" /> are not greater
	///     than zero.
	/// </exception>
	public Grid2D(int width, int height, bool usePooling = false)
	{
		if (width <= 0 || height <= 0)
			ThrowInvalidDimensions();

		Width     = width;
		Height    = height;
		Length    = checked(width * height);
		_fromPool = usePooling;
		_data     = usePooling ? ArrayPool<T>.Shared.Rent(Length) : new T[Length];
		if (usePooling)
			_data.AsSpan(0, Length).Clear();
	}

	/// <summary>
	///     Initializes a new instance of the <see cref="Grid2D{T}" /> class with the specified dimensions and default value
	///     for all elements.
	/// </summary>
	/// <param name="width">The width (number of columns) of the grid. Must be greater than zero.</param>
	/// <param name="height">The height (number of rows) of the grid. Must be greater than zero.</param>
	/// <param name="defaultValue">The default value to assign to all elements in the grid.</param>
	/// <param name="usePooling">Whether to rent the backing array from <see cref="ArrayPool{T}" />.</param>
	/// <exception cref="ArgumentException">
	///     Thrown if <paramref name="width" /> or <paramref name="height" /> are not greater
	///     than zero.
	/// </exception>
	public Grid2D(int width, int height, T defaultValue, bool usePooling = false) : this(width, height, usePooling)
	{
		AsSpan().Fill(defaultValue);
	}

	/// <summary>
	///     Gets the height (number of rows) of the grid.
	/// </summary>
	public int Height { get; }

	/// <summary>
	///     Gets the width (number of columns) of the grid.
	/// </summary>
	public int Width { get; }

	/// <summary>
	///     Gets the total number of elements in the grid (Width * Height).
	/// </summary>
	public int Length { get; }

	/// <summary>
	///     Gets a reference to the element at the specified <paramref name="x" /> and <paramref name="y" /> coordinates.
	/// </summary>
	/// <param name="x">The column index (zero-based).</param>
	/// <param name="y">The row index (zero-based).</param>
	/// <returns>A reference to the element located at the given coordinates.</returns>
	/// <exception cref="IndexOutOfRangeException">
	///     Thrown if the specified coordinates are outside the valid bounds of the
	///     grid.
	/// </exception>
	/// <exception cref="ObjectDisposedException">Thrown if the grid has been disposed.</exception>
	public ref T this[int x, int y]
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			ThrowIfDisposed();
			if (unchecked((uint)x) >= (uint)Width || unchecked((uint)y) >= (uint)Height)
				ThrowIndexOutOfRange();

			return ref _data![y * Width + x];
		}
	}

	/// <summary>
	///     Gets a <see cref="Span{T}" /> over the entire grid data in row-major order.
	/// </summary>
	/// <returns>A span over all grid elements.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Span<T> AsSpan()
	{
		ThrowIfDisposed();
		return _data.AsSpan(0, Length);
	}

	/// <summary>
	///     Gets a <see cref="ReadOnlySpan{T}" /> over the entire grid data in row-major order.
	/// </summary>
	/// <returns>A read-only span over all grid elements.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public ReadOnlySpan<T> AsReadOnlySpan() => AsSpan();

	/// <summary>
	///     Gets a <see cref="Span{T}" /> over a single row of the grid.
	/// </summary>
	/// <param name="y">The row index (zero-based).</param>
	/// <returns>A span over the specified row.</returns>
	/// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="y" /> is out of bounds.</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Span<T> GetRow(int y)
	{
		ThrowIfDisposed();
		if (unchecked((uint)y) >= (uint)Height)
			ThrowRowOutOfRange();

		return _data.AsSpan(y * Width, Width);
	}

	/// <summary>
	///     Gets a <see cref="ReadOnlySpan{T}" /> over a single row of the grid.
	/// </summary>
	/// <param name="y">The row index (zero-based).</param>
	/// <returns>A read-only span over the specified row.</returns>
	/// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="y" /> is out of bounds.</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public ReadOnlySpan<T> GetRowReadOnly(int y) => GetRow(y);

	/// <summary>
	///     Clears all elements in the grid to their default value.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Clear() => AsSpan().Clear();

	/// <summary>
	///     Fills all elements in the grid with the specified value.
	/// </summary>
	/// <param name="value">The value to fill the grid with.</param>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Fill(T value) => AsSpan().Fill(value);

	/// <summary>
	///     Checks if the specified coordinates are within the bounds of the grid.
	/// </summary>
	/// <param name="x">The column index.</param>
	/// <param name="y">The row index.</param>
	/// <returns><c>true</c> if the coordinates are valid; otherwise, <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool IsInBounds(int x, int y) => unchecked((uint)x) < (uint)Width && unchecked((uint)y) < (uint)Height;

	/// <summary>
	///     Tries to get the element at the specified coordinates without throwing.
	/// </summary>
	/// <param name="x">The column index.</param>
	/// <param name="y">The row index.</param>
	/// <param name="value">The value at the coordinates, if valid.</param>
	/// <returns><c>true</c> if the coordinates are valid; otherwise, <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool TryGet(int x, int y, out T value)
	{
		ThrowIfDisposed();
		if (IsInBounds(x, y))
		{
			value = _data![y * Width + x];
			return true;
		}

		value = default!;
		return false;
	}

	/// <summary>
	///     Tries to set the element at the specified coordinates without throwing.
	/// </summary>
	/// <param name="x">The column index.</param>
	/// <param name="y">The row index.</param>
	/// <param name="value">The value to set.</param>
	/// <returns><c>true</c> if the coordinates are valid and value was set; otherwise, <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool TrySet(int x, int y, T value)
	{
		ThrowIfDisposed();
		if (IsInBounds(x, y))
		{
			_data![y * Width + x] = value;
			return true;
		}

		return false;
	}

	/// <summary>
	///     Creates a <see cref="GridSpan2D{T}" /> view over this grid.
	/// </summary>
	/// <returns>A span-based view of the grid.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public GridSpan2D<T> AsGridSpan() => new(AsSpan(), Width, Height);

	/// <summary>
	///     Creates a <see cref="ReadOnlyGridSpan2D{T}" /> view over this grid.
	/// </summary>
	/// <returns>A read-only span-based view of the grid.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public ReadOnlyGridSpan2D<T> AsReadOnlyGridSpan() => new(AsReadOnlySpan(), Width, Height);

	/// <inheritdoc />
	public void Dispose()
	{
		var data = Interlocked.Exchange(ref _data, null);
		if (data is null)
			return;

		if (_fromPool)
			ArrayPool<T>.Shared.Return(data, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ThrowIfDisposed()
	{
		if (_data is null)
			throw new ObjectDisposedException(nameof(Grid2D<T>));
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void ThrowInvalidDimensions() =>
		throw new ArgumentException("Grid dimensions must be greater than zero.");

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void ThrowIndexOutOfRange() =>
		throw new IndexOutOfRangeException("Grid index out of range.");

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void ThrowRowOutOfRange() =>
		throw new ArgumentOutOfRangeException(nameof(ThrowRowOutOfRange), "Row index out of range.");
}

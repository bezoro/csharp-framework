using System.Runtime.CompilerServices;
using Bezoro.Core.Abstractions;
using Bezoro.Core.Helpers;

namespace Bezoro.Core.Extensions;

/// <summary>
///     Provides extension methods for arrays, including checking, conversion, processing, and searching.
/// </summary>
public static class ArrayExtensions
{
	#region Conversion

	/// <summary>
	///     Converts a two-dimensional array to a one-dimensional array.
	/// </summary>
	/// <typeparam name="T">The type of elements in the array.</typeparam>
	/// <param name="array">The two-dimensional array to convert.</param>
	/// <returns>A one-dimensional array containing all elements from the input array.</returns>
	public static T[] Flatten<T>(this T[,] array) =>
		ArrayConverter.Flatten(array);

	#endregion

	#region Check

	/// <summary>
	///     Checks if two single-dimensional arrays have the same length and contain equal elements in the same order.
	/// </summary>
	/// <param name="array">The source array to compare.</param>
	/// <param name="b">The target array to compare against.</param>
	/// <returns>true if arrays have same length and contain equal elements in the same order; otherwise, false.</returns>
	public static bool AreEqual<T>(this T[] array, T[] b)
	{
		array.ThrowIfNull();
		b.ThrowIfNull();

		if (ReferenceEquals(array, b)) return true;
		if (array.Length != b.Length) return false;
		var comparer = EqualityComparer<T>.Default;
		for (int index = 0; index < array.Length; index++)
			if (!comparer.Equals(array[index], b[index])) return false;
		return true;
	}

	/// <summary>
	///     Checks if two two-dimensional arrays have the same dimensions and contain equal elements at corresponding
	///     positions.
	/// </summary>
	/// <param name="array2d">The source two-dimensional array to compare.</param>
	/// <param name="b">The target two-dimensional array to compare against.</param>
	/// <returns>true if arrays have same dimensions and contain equal elements at corresponding positions; otherwise, false.</returns>
	public static bool AreEqual<T>(this T[,] array2d, T[,] b)
	{
		array2d.ThrowIfNull();
		b.ThrowIfNull();

		if (ReferenceEquals(array2d, b)) return true;

		if (array2d.Length != b.Length) return false;
		if (array2d.GetLength(0) != b.GetLength(0) ||
			array2d.GetLength(1) != b.GetLength(1)) return false;

		int rows = array2d.GetLength(0);
		int cols = array2d.GetLength(1);

		var comparer = EqualityComparer<T>.Default;

		for (var i = 0; i < rows; i++)
		{
			for (var j = 0; j < cols; j++)
			{
				if (!comparer.Equals(array2d[i, j], b[i, j]))
					return false;
			}
		}

		return true;
	}

	/// <summary>
	///     Checks if a 2D array is null or empty.
	/// </summary>
	/// <typeparam name="T">The type of elements in the array.</typeparam>
	/// <param name="array2d">The 2D array to check.</param>
	/// <returns>True if the array is null or empty, false otherwise.</returns>
	public static bool IsNullOrEmpty<T>(this T[,]? array2d) =>
		array2d == null || array2d.Length == 0;

	#endregion

	#region Process

	/// <summary>
	///     Processes all non-null elements of a 2D array in their order of appearance.
	/// </summary>
	/// <typeparam name="T">The type of elements in the array.</typeparam>
	/// <param name="array">The array to process.</param>
	/// <param name="processFunc">
	///     A function that takes an element of the array, its x- and y-coordinates, and a
	///     CancellationToken, and returns a Task. Null elements are skipped during processing.
	/// </param>
	/// <param name="cancellationToken">A CancellationToken that can be used to cancel the processing.</param>
	/// <returns>A Task that represents the asynchronous operation.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static async Task ProcessAsync<T>(
		this T[,]                                  array,
		Func<T, int, int, CancellationToken, Task> processFunc,
		CancellationToken                          cancellationToken = default
	)
	{
		array.ThrowIfNull();
		processFunc.ThrowIfNull();
		cancellationToken.ThrowIfCancellationRequested();
		int rows = array.GetLength(0);
		int cols = array.GetLength(1);

		for (var y = 0; y < rows; y++)
		{
			for (var x = 0; x < cols; x++)
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (array[y, x] is null) continue;

				await processFunc(array[y, x], x, y, cancellationToken).ConfigureAwait(false);
			}
		}
		cancellationToken.ThrowIfCancellationRequested();
	}

	/// <summary>
	///     Asynchronously processes all non-null elements in a one-dimensional array.
	/// </summary>
	/// <typeparam name="T">The type of elements in the array.</typeparam>
	/// <param name="array">The array to process.</param>
	/// <param name="action">The asynchronous action to perform on each non-null element.</param>
	/// <returns>A Task representing the asynchronous operation.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static async Task ProcessArrayAsync<T>(this T?[] array, Func<T, Task> action)
	{
		array.ThrowIfNull();
		action.ThrowIfNull();

		for (var i = 0; i < array.Length; i++)
		{
			var t = array[i];
			if (t.IsNull()) continue;

			await action(t).ConfigureAwait(false);
		}
	}

	/// <summary>
	///     Synchronously processes all non-null elements in a one-dimensional array.
	/// </summary>
	/// <typeparam name="T">The type of elements in the array.</typeparam>
	/// <param name="array">The array to process.</param>
	/// <param name="action">The synchronous action to perform on each non-null element.</param>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static void ProcessArray<T>(this T?[] array, Action<T> action)
	{
		array.ThrowIfNull();
		action.ThrowIfNull();

		foreach (var t in array)
		{
			if (t.IsNull()) continue;

			action(t);
		}
	}

	/// <summary>
	///     Processes all non-null elements in a one-dimensional array that implement IProcessable.
	/// </summary>
	/// <typeparam name="T">The type of elements in the array, must implement IProcessable.</typeparam>
	/// <param name="array">The array containing IProcessable elements to process.</param>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static void ProcessArray<T>(this T?[] array) where T : IProcessable
	{
		if (array is null) throw new ArgumentNullException(nameof(array));

		foreach (var element in array)
		{
			if (element is null) continue;

			element.Process();
		}
	}

	#endregion

	#region Search

	/// <summary>
	///     Searches for the specified element in the array using Equals method and returns its index.
	/// </summary>
	/// <typeparam name="T">The type of elements in the array. Must be a reference type.</typeparam>
	/// <param name="array">The array to search in. Cannot be null.</param>
	/// <param name="element">The element to find. Cannot be null.</param>
	/// <param name="index">
	///     When this method returns, contains the zero-based index of the found element in the array, or -1 if the element was
	///     not found.
	/// </param>
	/// <returns>true if the element was found; otherwise, false.</returns>
	public static bool Contains<T>(this T[] array, T element, out int index)
		where T : class
	{
		array.ThrowIfNull();
		element.ThrowIfNull();

		index = -1;

		for (var i = 0; i < array.Length; i++)
		{
			bool isMatch = EqualityComparer<T>.Default.Equals(array[i], element);
			if (!isMatch) continue;

			index = i;
			return true;
		}

		return false;
	}

	/// <summary>
	///     Searches for the specified struct element in the array and returns its index.
	/// </summary>
	/// <typeparam name="T">The type of elements in the array. Must be a value type implementing IEquatable{T}.</typeparam>
	/// <param name="array">The array to search in.</param>
	/// <param name="element">The element to find.</param>
	/// <param name="index">
	///     When this method returns, contains the index of the found element, or -1 if the element was not found.
	/// </param>
	/// <returns>true if the element was found; otherwise, false.</returns>
	public static bool ContainsStruct<T>(this T[] array, T element, out int index)
		where T : struct, IEquatable<T>
	{
		array.ThrowIfNull();
		element.ThrowIfNull();

		index = -1;

		for (var i = 0; i < array.Length; i++)
		{
			if (!array[i].Equals(element)) continue;

			index = i;
			return true;
		}

		return false;
	}

	/// <summary>
	///     Attempts to find the first null element in the array and returns its index.
	/// </summary>
	/// <typeparam name="T">The type of elements in the array. Must be a reference type.</typeparam>
	/// <param name="array">The array to search in.</param>
	/// <param name="index">
	///     When this method returns, contains the index of the first null element, or -1 if no null elements
	///     were found.
	/// </param>
	/// <returns>true if a null element was found; otherwise, false.</returns>
	public static bool TryFindFirstEmptyIndex<T>(this T?[] array, out int index)
		where T : class
	{
		array.ThrowIfNull();
		index = -1;

		if (array.Length == 0) return false;

		for (var i = 0; i < array.Length; i++)
		{
			var element = array[i];
			if (element.IsNull())
			{
				index = i;
				return true;
			}
		}

		return false;
	}

	/// <summary>
	///     Counts the number of empty (null) indices in the array.
	/// </summary>
	public static int CountEmptyIndices<T>(this T?[] array)
		where T : class
	{
		array.ThrowIfNull();

		var emptyIndicesCount = 0;

		foreach (var t in array)
		{
			if (t.IsNull())
				emptyIndicesCount++;
		}

		return emptyIndicesCount;
	}

	/// <summary>
	///     Counts the number of empty indices based on a valid size.
	/// </summary>
	public static int CountEmptyIndices<T>(this T[] array, int validSize)
		where T : class
	{
		array.ThrowIfNull();
		validSize.ThrowIfLessThan(1);
		validSize.ThrowIfMoreThan(array.Length);

		return array.Length - validSize;
	}

	/// <summary>
	///     Counts the number of filled (non-null) indices in the array.
	/// </summary>
	public static int CountFilledIndices<T>(this T?[] array)
		where T : class
	{
		array.ThrowIfNull();

		var filledIndicesCount = 0;

		foreach (var t in array)
		{
			if (!t.IsNull())
				filledIndicesCount++;
		}

		return filledIndicesCount;
	}

	#endregion
}

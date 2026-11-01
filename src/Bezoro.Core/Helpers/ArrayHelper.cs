using System.Numerics;
using System.Runtime.CompilerServices;
using Bezoro.Core.Extensions;

namespace Bezoro.Core.Helpers;

/// <summary>
///     Provides helper methods for common fixed-size array operations such as comparison, addition, removal, and
///     uniqueness.
///     All methods validate their input arrays for null or emptiness.
/// </summary>
public static class ArrayHelper
{
	/// <summary>
	///     Compares two arrays for equality by element.
	/// </summary>
	/// <typeparam name="T">Type of the array elements.</typeparam>
	/// <param name="first">The first array.</param>
	/// <param name="second">The second array.</param>
	/// <returns>True if arrays are equal in length and sequence; otherwise, false.</returns>
	/// <exception cref="ArgumentNullException">Thrown if either array is null.</exception>
	/// <exception cref="ArgumentException">Thrown if either array is empty.</exception>
	public static bool CompareArrays<T>(T[] first, T[] second)
	{
		ValidateArray(first);
		ValidateArray(second);

		if (first.Length != second.Length) return false;
		var comparer = EqualityComparer<T>.Default;

		for (var i = 0; i < first.Length; i++)
		{
			if (!comparer.Equals(first[i], second[i]))
				return false;
		}

		return true;
	}

	/// <summary>
	///     Calculates the squared Euclidean distance between two 3D points.
	///     This method is optimized for performance and avoids the square root operation.
	/// </summary>
	/// <param name="a">The first 3D point.</param>
	/// <param name="b">The second 3D point.</param>
	/// <returns>The squared distance between the two points.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static float DistanceSquared(Vector3 a, Vector3 b)
	{
		float dx = a.X - b.X;
		float dy = a.Y - b.Y;
		float dz = a.Z - b.Z;
		return dx * dx + dy * dy + dz * dz;
	}

	/// <summary>
	///     Removes the specified item from the array by setting its first occurrence to null.
	/// </summary>
	/// <typeparam name="T">The type of elements in the array (must be a reference type).</typeparam>
	/// <param name="array">The array to modify (passed by reference).</param>
	/// <param name="item">The item to remove.</param>
	/// <returns>The same array instance, with the specified item set to null if found; otherwise, unchanged.</returns>
	/// <exception cref="ArgumentNullException">Thrown if the array is null.</exception>
	/// <exception cref="ArgumentException">Thrown if the array is empty.</exception>
	public static T?[] RemoveElement<T>(ref T?[] array, T item) where T : class
	{
		ValidateArray(array);

		int index                    = Array.IndexOf(array, item);
		if (index >= 0) array[index] = null;

		return array;
	}

	/// <summary>
	///     Adds an item to the first available empty (null) slot in the array, or throws if the array is full.
	/// </summary>
	/// <typeparam name="T">The type of elements in the array.</typeparam>
	/// <param name="array">The array to modify (passed by reference).</param>
	/// <param name="item">The item to add.</param>
	/// <returns>The same array instance, with the item added to the first empty slot.</returns>
	/// <exception cref="ArgumentNullException">Thrown if the array is null.</exception>
	/// <exception cref="ArgumentException">Thrown if the array is empty.</exception>
	/// <exception cref="ArrayIsFullException">Thrown if the array is full (no empty slot).</exception>
	public static T[] Add<T>(ref T[] array, T item)
	{
		ValidateArray(array);

		return TryAddToFirstEmptySlot(ref array, item) ? array : throw new ArrayIsFullException();
	}

	/// <summary>
	///     Adds an item to the first available empty (null) slot in the array if it doesn't already exist;
	///     throws if the item exists or the array is full.
	/// </summary>
	/// <typeparam name="T">The type of elements in the array.</typeparam>
	/// <param name="array">The array to modify (passed by reference).</param>
	/// <param name="item">The item to add.</param>
	/// <returns>The same array instance, with the item added to the first empty slot.</returns>
	/// <exception cref="ArgumentNullException">Thrown if the array is null.</exception>
	/// <exception cref="ArgumentException">Thrown if the array is empty.</exception>
	/// <exception cref="ArrayIsFullException">Thrown if the array is full or the item already exists.</exception>
	public static T[] AddUnique<T>(ref T[] array, T item)
	{
		ValidateArray(array);

		if (Array.IndexOf(array, item) >= 0 || TryAddToFirstEmptySlot(ref array, item))
			return array;

		throw new ArrayIsFullException();
	}

	/// <summary>
	///     Attempts to add an item to the first available empty (null) slot in the array.
	/// </summary>
	/// <typeparam name="T">The type of elements in the array.</typeparam>
	/// <param name="array">The array to search for an empty slot (passed by reference).</param>
	/// <param name="item">The item to add.</param>
	/// <returns>True if the item was added; otherwise, false (array is full).</returns>
	private static bool TryAddToFirstEmptySlot<T>(ref T[] array, T item)
	{
		int count = array.Length;
		for (var i = 0; i < count; i++)
		{
			if (!array[i].IsNull()) continue;

			array[i] = item;
			return true;
		}

		return false;
	}

	/// <summary>
	///     Ensures the array is not null and not empty.
	/// </summary>
	/// <typeparam name="T">The type of elements in the array.</typeparam>
	/// <param name="array">The array to validate.</param>
	/// <exception cref="ArgumentNullException">Thrown if the array is null.</exception>
	/// <exception cref="ArgumentException">Thrown if the array is empty.</exception>
	private static void ValidateArray<T>(T[] array)
	{
		array.ThrowIfNull().ThrowIfEmpty();
	}
}

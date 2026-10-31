using System.Collections;
using System.Runtime.CompilerServices;

// ReSharper disable PossibleMultipleEnumeration

namespace Bezoro.Core.Extensions;

/// <summary>
///     Provides extension methods for <see cref="IEnumerable" /> and <see cref="IEnumerable{T}" /> for
///     common operations such as checking for contents, getting count, and pretty-printing.
/// </summary>
public static class EnumerableExtensions
{
	/// <summary>
	///     Returns <c>true</c> if <paramref name="source" /> contains at least one element.
	///     Uses <see cref="TryGetCount(IEnumerable, out int)" /> when possible to avoid enumeration.
	/// </summary>
	/// <param name="source">The non-generic enumerable to check for any elements.</param>
	/// <returns><c>true</c> if the sequence contains any elements; otherwise, <c>false</c>.</returns>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="source" /> is <c>null</c>.</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool HasAny(this IEnumerable source)
	{
		source.ThrowIfNull();

		if (source.TryGetCount(out int count)) return count != 0;
		if (source is ICollection c) return c.Count != 0;
		if (source is string s) return s.Length != 0;

		foreach (object? _ in source)
			return true;

		return false;
	}

	/// <summary>
	///     Returns <c>true</c> if the generic <paramref name="source" /> contains at least one element.
	///     Uses <see cref="HasAny(IEnumerable)" /> with type-casting.
	/// </summary>
	/// <typeparam name="T">The element type of the sequence.</typeparam>
	/// <param name="source">The generic enumerable to check for any elements.</param>
	/// <returns><c>true</c> if the sequence contains any elements; otherwise, <c>false</c>.</returns>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="source" /> is <c>null</c>.</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool HasAny<T>(this IEnumerable<T> source)
	{
		source.ThrowIfNull();
		if (source.TryGetCount(out int count)) return count != 0;
		using var enumerator = source.GetEnumerator();
		return enumerator.MoveNext();
	}

	/// <summary>
	///     Returns <c>true</c> when the sequence is <c>null</c> or has no elements.
	///     Enumerates at most one element when the count cannot be obtained cheaply.
	/// </summary>
	/// <param name="source">The (nullable) non-generic enumerable to check.</param>
	/// <returns><c>true</c> if <paramref name="source" /> is <c>null</c> or empty; otherwise, <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsNullOrEmpty(this IEnumerable? source)
		=> source is null || !source.HasAny();

	/// <summary>
	///     Returns <c>true</c> when the sequence is <c>null</c> or has no elements.
	///     Enumerates at most one element when the count cannot be obtained cheaply.
	/// </summary>
	/// <typeparam name="T">The element type of the sequence.</typeparam>
	/// <param name="source">The (nullable) generic enumerable to check.</param>
	/// <returns><c>true</c> if <paramref name="source" /> is <c>null</c> or empty; otherwise, <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool IsNullOrEmpty<T>(this IEnumerable<T>? source)
		=> source is null || !source.HasAny();

	/// <summary>
	///     Attempts to retrieve a count of elements for the given <see cref="IEnumerable" />.
	///     Uses known interfaces for O(1) count retrieval.
	/// </summary>
	/// <param name="source">The non-generic enumerable whose item count to try to retrieve.</param>
	/// <param name="count">When this method returns, contains the count if retrieval succeeded; otherwise, <c>-1</c>.</param>
	/// <returns><c>true</c> if the count was obtained without enumeration; otherwise, <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool TryGetCount(this IEnumerable source, out int count)
	{
		switch (source)
		{
			case ICollection c:
				count = c.Count;
				return true;
			case string s: // IEnumerable<char>
				count = s.Length;
				return true;
			default:
				count = -1;
				return false;
		}
	}

	/// <summary>
	///     Attempts to retrieve a count of elements for the given <see cref="IEnumerable{T}" />.
	///     Uses <see cref="ICollection{T}" />, <see cref="IReadOnlyCollection{T}" />, and <see cref="ICollection" /> for O(1)
	///     retrieval.
	///     Handles special-case for <see cref="string" /> when <typeparamref name="T" /> is <see cref="char" />.
	/// </summary>
	/// <typeparam name="T">The element type of the sequence.</typeparam>
	/// <param name="source">The generic enumerable whose item count to try to retrieve.</param>
	/// <param name="count">When this method returns, contains the count if retrieval succeeded; otherwise, <c>-1</c>.</param>
	/// <returns><c>true</c> if the count was obtained without enumeration; otherwise, <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static bool TryGetCount<T>(this IEnumerable<T> source, out int count)
	{
		switch (source)
		{
			case ICollection<T> c:
				count = c.Count;
				return true;
			case IReadOnlyCollection<T> ro:
				count = ro.Count;
				return true;
			case ICollection nonGeneric:
				count = nonGeneric.Count;
				return true;
			// Special-case strings when T == char
			case string s when typeof(T) == typeof(char):
				count = s.Length;
				return true;
			default:
				count = -1;
				return false;
		}
	}

	/// <summary>
	///     Concatenates the members of a collection, using the specified separator between each element.
	/// </summary>
	/// <typeparam name="T">The element type of the sequence.</typeparam>
	/// <param name="source">The sequence whose elements to join as a string.</param>
	/// <param name="separator">The string separator to use between each element. Default is ", ".</param>
	/// <returns>
	///     A string composed of the string representations of the sequence elements, separated by
	///     <paramref name="separator" />.
	/// </returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static string PrettyJoin<T>(this IEnumerable<T> source, string separator = ", ")
		=> string.Join(separator, source);
}

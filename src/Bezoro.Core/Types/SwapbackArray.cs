using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Bezoro.Core.Types;

/// <summary>
///     Represents a dynamic array supporting fast O(1) removal by swapping the last element into the position of the
///     removed item.
///     This array does not preserve element order but offers efficient add and remove operations. Capacity expands
///     automatically.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
/// <remarks>
///     The array is ideal for scenarios where element order is unimportant and high-performance removals are desired.
///     Implements <see cref="IReadOnlyList{T}" /> for enumeration. Use <see cref="Add(T)" />, <see cref="Remove(T)" />,
///     <see cref="Clear()" />, etc. for mutation.
///     Storage and borrowed views require exclusive ownership. Predicates must not mutate the array.
/// </remarks>
[DebuggerDisplay("Count = {Count}, Capacity = {Capacity}")]
public sealed class SwapbackArray<T> : IReadOnlyList<T>
{
	private const uint MAX_ARRAY_LENGTH   = 0x7FFFFFC7; // Maximum array size per .NET implementation details.
	private const uint MINIMUM_ARRAY_SIZE = 4u;

	/// <summary>
	///     Internal data storage for elements, automatically resized as needed.
	/// </summary>
	private T[] _items;

	/// <summary>
	///     The number of active elements in the array.
	/// </summary>
	private uint _count;

	/// <summary>
	///     Initializes a new empty instance, optionally with a specified initial capacity.
	/// </summary>
	/// <param name="initialCapacity">Initial number of reserved slots (minimum 4).</param>
	public SwapbackArray(uint initialCapacity = MINIMUM_ARRAY_SIZE)
	{
		uint capacity = Math.Max(initialCapacity, MinimumArraySize);
		_items  = new T[capacity];
		_count  = 0;
		Version = 0;
	}

	/// <summary>
	///     Initializes from an <see cref="ICollection{T}" />, copying its elements.
	/// </summary>
	/// <param name="collection">Source collection. Cannot be null.</param>
	/// <exception cref="ArgumentNullException">If <paramref name="collection" /> is null.</exception>
	public SwapbackArray(ICollection<T> collection)
	{
		if (collection is null) throw new ArgumentNullException(nameof(collection));

		var capacity = (uint)Math.Max(collection.Count, MinimumArraySize);
		_items = new T[capacity];
		_count = (uint)collection.Count;

		if (_count > 0)
			collection.CopyTo(_items, 0);

		Version = 0;
	}

	/// <summary>
	///     Initializes the array from any enumerable, copying its contents.
	/// </summary>
	/// <param name="collection">Source sequence. Cannot be null.</param>
	/// <exception cref="ArgumentNullException">If <paramref name="collection" /> is null.</exception>
	public SwapbackArray(IEnumerable<T> collection)
	{
		switch (collection)
		{
			case null:
				throw new ArgumentNullException(nameof(collection));
			case ICollection<T> c:
			{
				var capacity = (uint)Math.Max(c.Count, MinimumArraySize);
				_items = new T[capacity];
				_count = (uint)c.Count;

				if (_count > 0)
					c.CopyTo(_items, 0);

				break;
			}
			default:
			{
				_items = new T[MinimumArraySize];
				_count = 0;
				foreach (var item in collection)
					Add(item);

				break;
			}
		}

		Version = 0;
	}

	/// <summary>
	///     Returns true if the array is empty.
	/// </summary>
	public bool IsEmpty => _count == 0;

	/// <summary>
	///     Returns true if the array is full (all capacity in use).
	/// </summary>
	public bool IsFull => _count == Capacity;

	/// <summary>
	///     Gets the internal array's total capacity.
	/// </summary>
	public uint Capacity => (uint)_items.Length;

	/// <summary>
	///     Gets the current number of elements.
	/// </summary>
	public uint Count => _count;

	/// <summary>
	///     The absolute maximum supported array size.
	/// </summary>
	public uint MaxArrayLength => MAX_ARRAY_LENGTH;

	/// <summary>
	///     The minimum allowed capacity for an internal array.
	/// </summary>
	public uint MinimumArraySize => MINIMUM_ARRAY_SIZE;

	/// <inheritdoc />
	int IReadOnlyCollection<T>.Count => (int)_count;

	/// <summary>
	///     Gets or sets the low-utilization threshold percentage for automatic shrinking after removals.
	/// </summary>
	public Percent ShrinkThresholdPercent { get; set; } = Percent.Quarter;

	/// <summary>
	///     Modified on each mutating operation; used to detect changes during enumeration.
	/// </summary>
	public uint Version { get; private set; }

	/// <summary>
	///     Accesses the element at the given zero-based <paramref name="index" />.
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">If <paramref name="index" /> is not valid.</exception>
	public T this[uint index]
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get
		{
			if (index >= _count) throw new ArgumentOutOfRangeException(nameof(index));

			return _items[index];
		}
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		set
		{
			if (index >= _count) throw new ArgumentOutOfRangeException(nameof(index));

			_items[index] = value;
			Version = unchecked(Version + 1);
		}
	}

	/// <inheritdoc />
	T IReadOnlyList<T>.this[int index]
	{
		get
		{
			if (index < 0 || index >= (int)_count)
				throw new ArgumentOutOfRangeException(nameof(index));

			return this[(uint)index];
		}
	}

	/// <summary>
	///     Returns true if the array contains the given <paramref name="item" />.
	///     Uses the default equality comparer.
	/// </summary>
	public bool Contains(T item) => FindIndex(item) >= 0;

	/// <summary>
	///     Attempts to get the value at <paramref name="index" />. Returns success and the value if found.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool TryGet(uint index, out T? value)
	{
		if (index >= _count)
		{
			value = default!;
			return false;
		}

		value = _items[index];
		return true;
	}

	/// <summary>
	///     Attempts to find <paramref name="item" />; returns true/false and its zero-based index if found.
	///     Uses the default equality comparer.
	/// </summary>
	/// <param name="item">The value to find.</param>
	/// <param name="index">The zero-based index when found; otherwise zero.</param>
	/// <returns>True if <paramref name="item" /> is found; otherwise false.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool TryGetIndex(T item, out uint index)
	{
		int found = FindIndex(item);
		index = found >= 0 ? (uint)found : 0;
		return found >= 0;
	}

	/// <summary>
	///     Attempts to find <paramref name="item" /> and returns its zero-based index when found.
	/// </summary>
	/// <param name="item">The value to find.</param>
	/// <param name="index">The zero-based index when found; otherwise null.</param>
	/// <returns>True if <paramref name="item" /> is found; otherwise false.</returns>
	[Obsolete("Use TryGetIndex(T, out uint) instead.")]
	public bool TryIndexOf(T item, out uint? index)
	{
		bool found = TryGetIndex(item, out uint value);
		index = found ? value : null;
		return found;
	}

	/// <summary>
	///     Attempts to retrieve the last item in the array without removing it.
	/// </summary>
	/// <param name="value">Receives the last value, or default(T) if empty.</param>
	/// <returns>True if there is at least one element; otherwise false.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool TryPeek(out T? value)
	{
		if (_count == 0)
		{
			value = default;
			return false;
		}

		value = _items[_count - 1];
		return true;
	}

	/// <summary>
	///     Attempts to remove and return the last element.
	/// </summary>
	/// <param name="value">Receives the removed value if present.</param>
	/// <returns>True if successful.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool TryPopBack(out T value)
	{
		if (_count == 0)
		{
			value = default!;
			return false;
		}

		_count--;
		value = _items[_count];
		if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
			_items[_count] = default!;

		Version = unchecked(Version + 1);
		MaybeShrink();
		return true;
	}

	/// <summary>
	///     Removes the first matching occurrence of <paramref name="item" />. Uses the default equality comparer.
	///     This is an O(n) search, but actual removal is O(1).
	/// </summary>
	/// <returns>True if removal succeeded, false if not found.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool TryRemove(T item)
	{
		int index = FindIndex(item);
		return index >= 0 && TryRemoveAt((uint)index);
	}

	/// <summary>
	///     Removes the item at the specified <paramref name="index">index</paramref> by swapping the last element into its
	///     place.
	/// </summary>
	/// <returns>True if successful; false if <paramref name="index" /> is invalid.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool TryRemoveAt(uint index)
	{
		if (index >= _count) return false;

		_count--;

		if (index != _count)
			_items[index] = _items[_count];

		if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
			_items[_count] = default!;

		Version = unchecked(Version + 1);
		MaybeShrink();
		return true;
	}

	/// <summary>
	///     Returns a read-only collection view over this dynamic array.
	/// </summary>
	public IReadOnlyCollection<T> AsReadOnlyCollection() => this;

	/// <summary>
	///     Returns a read-only span over the active elements.
	/// </summary>
	public ReadOnlySpan<T> AsSpan() => new(_items, 0, (int)_count);

	/// <summary>
	///     Returns a mutable span over the active elements. Changes do not affect the logical version or enumeration.
	/// </summary>
	public Span<T> AsMutableSpanUnsafe() => new(_items, 0, (int)_count);

	/// <summary>
	///     Gets a struct enumerator for fast array iteration.
	/// </summary>
	public SwapbackArrayEnumerator GetEnumerator() => new(this);

	/// <summary>
	///     Copies elements into a new array of exact length and returns it.
	/// </summary>
	public T[] ToArray()
	{
		var result = new T[_count];
		Array.Copy(_items, 0, result, 0, (int)_count);
		return result;
	}

	/// <summary>
	///     Returns the zero-based index of <paramref name="item" />, or throws if not found.
	/// </summary>
	/// <param name="item">The value to find.</param>
	/// <returns>The zero-based index.</returns>
	/// <exception cref="InvalidOperationException">If the array is empty.</exception>
	/// <exception cref="ArgumentException">If not found.</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public uint IndexOf(T item)
	{
		if (_count == 0)
			throw new InvalidOperationException("Array is empty.");

		int index = FindIndex(item);

		if (index >= 0) return (uint)index;

		throw new ArgumentException("Item not found.");
	}

	/// <summary>
	///     Removes all elements matching <paramref name="match" /> using swapback logic.
	///     Returns the number of elements removed.
	/// </summary>
	/// <param name="match">Predicate to select elements for removal.</param>
	/// <exception cref="ArgumentNullException">If <paramref name="match" /> is null.</exception>
	/// <exception cref="InvalidOperationException">The predicate mutates the array.</exception>
	/// <remarks>Compaction is in place; work performed before a predicate failure is not rolled back.</remarks>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public uint RemoveAll(Predicate<T> match)
	{
		if (match is null) throw new ArgumentNullException(nameof(match));

		if (_count == 0) return 0;

		uint originalCount = _count;
		uint newCount      = CompactArray(match, originalCount);
		uint removedCount  = originalCount - newCount;

		if (removedCount > 0)
			FinalizeRemoval(newCount, originalCount);

		return removedCount;
	}

	/// <summary>
	///     Adds a single item to the end. Increases capacity if needed.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Add(T item)
	{
		EnsureCapacity(_count + 1);
		_items[_count++] = item;
		Version = unchecked(Version + 1);
	}

	/// <summary>
	///     Adds a range of items from a <see cref="ReadOnlySpan{T}" />.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void AddRange(ReadOnlySpan<T> span)
	{
		if (span.Length == 0) return;

		EnsureCapacity(_count + (uint)span.Length);
		span.CopyTo(new(_items, (int)_count, span.Length));
		_count += (uint)span.Length;
		Version = unchecked(Version + 1);
	}

	/// <summary>
	///     Adds a range of items from an enumerable collection.
	/// </summary>
	/// <param name="collection">Source items to append.</param>
	/// <exception cref="ArgumentNullException">If <paramref name="collection" /> is null.</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void AddRange(IEnumerable<T> collection)
	{
		if (collection is null)
			throw new ArgumentNullException(nameof(collection));

		if (ReferenceEquals(collection, this))
		{
			AddRange(AsSpan());
			return;
		}

		uint startCount = _count;
		uint startVersion = Version;
		try
		{
			_ = collection switch
			{
				ICollection<T> c           => TryAddCollection(c),
				IReadOnlyCollection<T> roc => TryAddReadOnlyCollection(roc),
				_                          => AddEnumerable(collection)
			};
		}
		finally
		{
			if (_count != startCount && Version == startVersion)
				Version = unchecked(Version + 1);
		}
	}

	/// <summary>
	///     Adds an item without checking or resizing capacity. Caller must guarantee room.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void AddUnchecked(T item)
	{
		_items[_count++] = item;
		Version = unchecked(Version + 1);
	}

	/// <summary>
	///     Removes all elements and shrinks capacity to the minimum size.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Clear() => ClearCore(trim: true);

	/// <summary>
	///     Removes all elements while retaining the current capacity.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void ClearRetainingCapacity() => ClearCore(trim: false);

	/// <summary>
	///     Removes all elements, optionally shrinking capacity to the minimum size.
	/// </summary>
	/// <param name="trim">Whether to reset internal capacity to <see cref="MinimumArraySize" />.</param>
	[Obsolete("Use Clear() or ClearRetainingCapacity() instead.")]
	public void Clear(bool trim) => ClearCore(trim);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ClearCore(bool trim)
	{
		if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() && _count > 0)
			Array.Clear(_items, 0, (int)_count);

		_count = 0;
		Version = unchecked(Version + 1);
		if (trim && Capacity > MinimumArraySize)
			Resize(MinimumArraySize);
	}

	/// <summary>
	///     Copies all elements into the provided <see cref="Span{T}" />. The span must have sufficient capacity.
	/// </summary>
	/// <param name="destination">Destination span to receive elements.</param>
	/// <exception cref="ArgumentException">If <paramref name="destination" /> is not large enough.</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void CopyTo(Span<T> destination)
	{
		if (destination.Length < _count)
			throw new ArgumentException("Destination span is not large enough.");

		AsSpan().CopyTo(destination);
	}

	/// <summary>
	///     Copies elements into a target array, starting at the specified index of the destination.
	/// </summary>
	/// <param name="destination">Target array (not null).</param>
	/// <param name="destinationIndex">Start index in target array.</param>
	/// <exception cref="ArgumentNullException">If <paramref name="destination" /> is null.</exception>
	/// <exception cref="ArgumentException">If destination does not have enough space.</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void CopyTo(T[] destination, uint destinationIndex = 0)
	{
		if (destination is null) throw new ArgumentNullException(nameof(destination));
		if (destinationIndex > destination.Length ||
			_count > (uint)(destination.Length - (int)destinationIndex))
			throw new ArgumentException("Destination array is not large enough.");

		Array.Copy(_items, 0, destination, destinationIndex, (int)_count);
	}

	/// <summary>
	///     Ensures the internal array is at least <paramref name="min" /> elements large.
	/// </summary>
	/// <param name="min">Minimum required capacity.</param>
	/// <exception cref="OutOfMemoryException">If <paramref name="min" /> is too large.</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void EnsureCapacity(uint min)
	{
		var current = (uint)_items.Length;
		if (current >= min) return;

		Resize(CalculateExpandedCapacity(current, min));
	}

	internal static uint CalculateExpandedCapacity(uint current, uint min)
	{
		if (min > MAX_ARRAY_LENGTH)
			throw new OutOfMemoryException("Required capacity exceeds maximum array length.");
		if (current >= min)
			return current;

		uint newCapacity;
		if (current == 0)
			newCapacity = MINIMUM_ARRAY_SIZE;
		else
			newCapacity = current > MAX_ARRAY_LENGTH / 2
							  ? MAX_ARRAY_LENGTH
							  : Math.Min(current * 2, MAX_ARRAY_LENGTH);

		if (newCapacity < min) newCapacity = min;

		if (newCapacity > MAX_ARRAY_LENGTH)
		{
			if (min > MAX_ARRAY_LENGTH)
				throw new OutOfMemoryException("Required capacity exceeds maximum array length.");

			newCapacity = MAX_ARRAY_LENGTH;
		}

		return newCapacity;
	}

	/// <summary>
	///     Inserts an item at <paramref name="index" /> by moving the existing value to the end.
	///     If inserting at <see cref="Count" />, this simply appends the item.
	/// </summary>
	/// <param name="index">Insertion point.</param>
	/// <param name="item">Value to insert.</param>
	/// <exception cref="ArgumentOutOfRangeException">If <paramref name="index" /> is out of bounds.</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void InsertAt(uint index, T item)
	{
		if (index > _count)
			throw new ArgumentOutOfRangeException(nameof(index), "Index cannot be greater than Count.");

		EnsureCapacity(_count + 1);

		if (index == _count)
		{
			_items[_count] = item;
		}
		else
		{
			_items[_count] = _items[index];
			_items[index]  = item;
		}

		_count++;
		Version = unchecked(Version + 1);
	}

	/// <summary>
	///     Removes the first matching occurrence of <paramref name="item" />.
	/// </summary>
	/// <param name="item">The item to remove.</param>
	/// <exception cref="InvalidOperationException">If the item is not found.</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Remove(T item)
	{
		if (!TryRemove(item))
			throw new InvalidOperationException("The specified item was not found in the SwapbackArray.");
	}

	/// <summary>
	///     Replaces the element at <paramref name="index" />.
	/// </summary>
	/// <param name="index">Target index. Must be within range.</param>
	/// <param name="item">Replacement value.</param>
	/// <exception cref="ArgumentOutOfRangeException">If <paramref name="index" /> is out of range.</exception>
	public void ReplaceAt(uint index, T item)
	{
		if (index >= _count)
			throw new ArgumentOutOfRangeException(nameof(index), "Index must be less than Count.");

		_items[index] = item;
		Version = unchecked(Version + 1);
	}

	/// <summary>
	///     Trims capacity if usage falls below the supplied utilization percentage.
	///     This will never shrink below <see cref="MinimumArraySize" />.
	/// </summary>
	/// <param name="minimumUtilizationThreshold">
	///     Minimum allowed utilization as a <see cref="Percent" />. If omitted, defaults to <see cref="Percent.Ninety" />.
	/// </param>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void TrimExcess(Percent minimumUtilizationThreshold = default)
	{
		if (minimumUtilizationThreshold == default)
			minimumUtilizationThreshold = Percent.Ninety;

		var length = (uint)_items.Length;
		if (length <= MinimumArraySize) return;

		ulong left  = _count * 100UL;
		ulong right = length * minimumUtilizationThreshold.Value;
		if (left >= right) return;

		uint newSize = Math.Max(_count, MinimumArraySize);
		Resize(newSize);
	}

	/// <inheritdoc />
	IEnumerator IEnumerable.GetEnumerator() => new SwapbackArrayEnumerator(this);

	/// <inheritdoc />
	IEnumerator<T> IEnumerable<T>.GetEnumerator() => new SwapbackArrayEnumerator(this);

	/// <summary>
	///     Helper to add all items from an arbitrary enumerable.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool AddEnumerable(IEnumerable<T> collection)
	{
		uint startCount = _count;
		foreach (var item in collection)
		{
			EnsureCapacity(_count + 1);
			if (_count == startCount)
				Version = unchecked(Version + 1);

			_items[_count++] = item;
		}

		return _count != startCount;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private int FindIndex(T item)
	{
		var comparer = EqualityComparer<T>.Default;
		for (uint index = 0; index < _count; index++)
		{
			if (comparer.Equals(_items[index], item))
				return (int)index;
		}

		return -1;
	}

	/// <summary>
	///     Helper to add all items from an <see cref="ICollection{T}" /> in one operation.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryAddCollection(ICollection<T> collection)
	{
		if (collection.Count <= 0)
			return false;

		EnsureCapacity(_count + (uint)collection.Count);
		collection.CopyTo(_items, (int)_count);
		_count += (uint)collection.Count;
		return true;
	}

	/// <summary>
	///     Helper to add items from an <see cref="IReadOnlyCollection{T}" />.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryAddReadOnlyCollection(IReadOnlyCollection<T> collection)
	{
		if (collection.Count <= 0)
			return false;

		EnsureCapacity(_count + (uint)collection.Count);
		return AddEnumerable(collection);
	}

	/// <summary>
	///     Helper for <see cref="RemoveAll" />: Compacts the array by skipping/removing elements for which
	///     <paramref name="match" /> returns true.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private uint CompactArray(Predicate<T> match, uint originalCount)
	{
		uint write = 0;
		uint read  = 0;
		uint version = Version;

		while (read < originalCount)
		{
			bool remove = match(_items[read]);
			if (Version != version)
				throw new InvalidOperationException("The removal predicate must not mutate the array.");
			if (!remove)
			{
				if (write != read)
					_items[write] = _items[read];

				write++;
			}

			read++;
		}

		return write;
	}

	/// <summary>
	///     Clears a range of array slots by setting to default(T), if appropriate for type.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ClearRemovedSlots(uint startIndex, uint endIndex)
	{
		if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
			return;

		for (uint i = startIndex; i < endIndex; i++)
			_items[i] = default!;
	}

	/// <summary>
	///     Finalizes removal - clears old slots, adjusts count, version, and shrinks if warranted.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void FinalizeRemoval(uint newCount, uint originalCount)
	{
		ClearRemovedSlots(newCount, originalCount);
		_count = newCount;
		Version = unchecked(Version + 1);
		MaybeShrink();
	}

	/// <summary>
	///     Shrinks the array if utilization is below <see cref="ShrinkThresholdPercent" />.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void MaybeShrink()
	{
		var length = (uint)_items.Length;
		if (length <= MinimumArraySize) return;

		ulong left  = _count * 100UL;
		ulong right = length * (ulong)ShrinkThresholdPercent.Value;
		if (left > right) return;

		uint targetCapacity = Math.Max(_count * 2, MinimumArraySize);
		if (targetCapacity < length)
			Resize(targetCapacity);
	}

	/// <summary>
	///     Internal reallocation; sets capacity to <paramref name="newSize" />.
	/// </summary>
	/// <param name="newSize">New array size. Must not exceed <see cref="MaxArrayLength" />.</param>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void Resize(uint newSize)
	{
		if (newSize > MAX_ARRAY_LENGTH)
			throw new OutOfMemoryException("Array size exceeds maximum array length.");

		var newItems = new T[newSize];
		if (_count > 0)
			Array.Copy(_items, newItems, (int)_count);

		_items = newItems;
	}

	/// <summary>
	///     An enumerator for <see cref="SwapbackArray{T}" /> that checks for concurrent modification.
	/// </summary>
	public struct SwapbackArrayEnumerator : IEnumerator<T>
	{
		private readonly SwapbackArray<T> _array;
		private readonly uint             _version;
		private          uint             _index;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal SwapbackArrayEnumerator(SwapbackArray<T> array)
		{
			_array   = array;
			_version = array.Version;
			_index   = 0;
			Current  = default!;
		}

		/// <inheritdoc />
		object IEnumerator.Current => Current!;

		/// <summary>
		///     The element at the iterator's current position.
		/// </summary>
		public T Current { get; private set; }

		/// <summary>
		///     Advances to the next element; throws if the collection was modified during enumeration.
		/// </summary>
		/// <returns>True if successfully moved to the next element; false if at end.</returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool MoveNext()
		{
			if (_version != _array.Version)
				throw new InvalidOperationException("Collection was modified during enumeration.");

			if (_index < _array._count)
			{
				Current = _array._items[_index++];
				return true;
			}

			Current = default!;
			return false;
		}

		/// <inheritdoc />
		public void Dispose() { }

		/// <inheritdoc />
		public void Reset() => throw new NotSupportedException();
	}
}

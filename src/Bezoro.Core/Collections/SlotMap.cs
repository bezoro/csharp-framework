using System.Collections;

namespace Bezoro.Core.Collections;

/// <summary>Stores a fixed number of densely packed values addressed by stable, generation-checked handles.</summary>
/// <typeparam name="T">The stored value type.</typeparam>
/// <remarks>
/// Mutations require one owning thread or external serialization. Separate maps can run independently.
/// Removal moves the last dense value into the removed position; handles survive that move, dense indices do not.
/// The map owns its storage, but does not dispose values.
/// </remarks>
public sealed class SlotMap<T> : IReadOnlyCollection<T>
{
	private const int Free = -1;
	private const int Retired = -2;
	private readonly long _owner;
	private readonly T[] _values;
	private readonly int[] _denseToSlot;
	private readonly Slot[] _slots;
	private int _freeHead;
	private int _count;
	private int _retiredCount;
	private long _version;

	/// <summary>Creates an empty map with the supplied fixed capacity. Zero capacity is permitted.</summary>
	/// <exception cref="ArgumentOutOfRangeException">Capacity is negative.</exception>
	public SlotMap(int capacity) : this(capacity, 1) { }

	// The generation seam lets tests reach retirement without billions of remove/add cycles.
	internal SlotMap(int capacity, uint initialGeneration)
	{
		if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
		if (initialGeneration == 0) throw new ArgumentOutOfRangeException(nameof(initialGeneration));
		_owner = CollectionOwnerId.Next();
		_values = capacity == 0 ? Array.Empty<T>() : new T[capacity];
		_denseToSlot = capacity == 0 ? Array.Empty<int>() : new int[capacity];
		_slots = capacity == 0 ? Array.Empty<Slot>() : new Slot[capacity];
		_freeHead = capacity == 0 ? Free : 0;
		for (int index = 0; index < capacity; index++)
			_slots[index] = new Slot { DenseIndex = Free, NextFree = index + 1 < capacity ? index + 1 : Free, Generation = initialGeneration };
	}

	/// <summary>Gets the number of live values.</summary>
	public int Count => _count;

	/// <summary>Gets the original storage capacity. Storage never grows.</summary>
	public int Capacity => _values.Length;

	/// <summary>Gets the number of slots that can currently accept new values.</summary>
	/// <remarks>Slots whose generations are exhausted are permanently retired.</remarks>
	public int AvailableCapacity => Capacity - _count - _retiredCount;

	/// <summary>Gets a read-only view of live values in current dense order.</summary>
	/// <remarks>The view is borrowed and must not be retained across mutations.</remarks>
	public ReadOnlySpan<T> Values => _values.AsSpan(0, _count);

	/// <summary>Gets or replaces a value identified by a live handle.</summary>
	/// <exception cref="ArgumentException">The handle is stale, default, or belongs to another map.</exception>
	public T this[SlotHandle handle]
	{
		get => _values[GetDenseIndex(handle)];
		set
		{
			_values[GetDenseIndex(handle)] = value;
			_version = unchecked(_version + 1);
		}
	}

	/// <summary>Adds a value and returns its stable handle.</summary>
	/// <exception cref="InvalidOperationException">No reusable slot remains.</exception>
	public SlotHandle Add(T value)
	{
		if (!TryAdd(value, out SlotHandle handle))
			throw new InvalidOperationException("The slot map has no available slots.");
		return handle;
	}

	/// <summary>Adds a value if a slot is available, otherwise returns false and the default handle.</summary>
	public bool TryAdd(T value, out SlotHandle handle)
	{
		if (_freeHead == Free)
		{
			handle = default;
			return false;
		}
		int slotIndex = _freeHead;
		ref Slot slot = ref _slots[slotIndex];
		_freeHead = slot.NextFree;
		slot.DenseIndex = _count;
		slot.NextFree = Free;
		_denseToSlot[_count] = slotIndex;
		_values[_count++] = value;
		_version = unchecked(_version + 1);
		handle = new SlotHandle(_owner, slotIndex, slot.Generation);
		return true;
	}

	/// <summary>Reports whether a handle currently identifies a value in this map.</summary>
	public bool Contains(SlotHandle handle) =>
		handle.Owner == _owner && unchecked((uint)handle.Index) < (uint)_slots.Length &&
		_slots[handle.Index].DenseIndex >= 0 && _slots[handle.Index].Generation == handle.Generation;

	/// <summary>Copies a live value to the output, or returns false and the default value for an invalid handle.</summary>
	public bool TryGet(SlotHandle handle, out T value)
	{
		if (!Contains(handle))
		{
			value = default!;
			return false;
		}
		value = _values[_slots[handle.Index].DenseIndex];
		return true;
	}

	/// <summary>Gets the stable handle for a position in the current dense value view.</summary>
	/// <exception cref="ArgumentOutOfRangeException">The dense index is outside the live value range.</exception>
	public SlotHandle GetHandleAt(int denseIndex)
	{
		if (unchecked((uint)denseIndex) >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(denseIndex));
		int slotIndex = _denseToSlot[denseIndex];
		return new SlotHandle(_owner, slotIndex, _slots[slotIndex].Generation);
	}

	/// <summary>Removes a live value. Invalid handles return false without changing the map.</summary>
	public bool Remove(SlotHandle handle) => TryRemove(handle, out _);

	/// <summary>Removes a live value and returns it. Invalid handles return false and the default value.</summary>
	public bool TryRemove(SlotHandle handle, out T value)
	{
		if (!Contains(handle))
		{
			value = default!;
			return false;
		}
		int denseIndex = _slots[handle.Index].DenseIndex;
		value = _values[denseIndex];
		int last = --_count;
		if (denseIndex != last)
		{
			_values[denseIndex] = _values[last];
			int movedSlot = _denseToSlot[last];
			_denseToSlot[denseIndex] = movedSlot;
			_slots[movedSlot].DenseIndex = denseIndex;
		}
		_values[last] = default!;
		_denseToSlot[last] = 0;
		ReleaseSlot(handle.Index);
		_version = unchecked(_version + 1);
		return true;
	}

	/// <summary>Removes every value, clears references, and invalidates every live handle.</summary>
	/// <remarks>O(Count). Retired slots stay retired; no value is disposed.</remarks>
	public void Clear()
	{
		if (_count == 0) return;
		for (int denseIndex = 0; denseIndex < _count; denseIndex++)
			ReleaseSlot(_denseToSlot[denseIndex]);
		Array.Clear(_values, 0, _count);
		Array.Clear(_denseToSlot, 0, _count);
		_count = 0;
		_version = unchecked(_version + 1);
	}

	private int GetDenseIndex(SlotHandle handle)
	{
		if (!Contains(handle)) throw new ArgumentException("The handle does not identify a live value in this map.", nameof(handle));
		return _slots[handle.Index].DenseIndex;
	}

	private void ReleaseSlot(int index)
	{
		ref Slot slot = ref _slots[index];
		if (slot.Generation == uint.MaxValue)
		{
			slot.DenseIndex = Retired;
			slot.NextFree = Free;
			_retiredCount++;
			return;
		}
		slot.Generation++;
		slot.DenseIndex = Free;
		slot.NextFree = _freeHead;
		_freeHead = index;
	}

	/// <summary>Returns an allocation-free dense-order enumerator.</summary>
	public Enumerator GetEnumerator() => new(this);
	IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	/// <summary>Enumerates dense values and rejects mutations after enumeration starts.</summary>
	public struct Enumerator : IEnumerator<T>
	{
		private readonly SlotMap<T> _map;
		private readonly long _version;
		private int _index;

		internal Enumerator(SlotMap<T> map) { _map = map; _version = map._version; _index = -1; }

		/// <inheritdoc />
		public readonly T Current
		{
			get
			{
				EnsureUnchanged();
				if (unchecked((uint)_index) >= (uint)_map._count) throw new InvalidOperationException("The enumerator is not positioned on a value.");
				return _map._values[_index];
			}
		}
		readonly object? IEnumerator.Current => Current;

		/// <inheritdoc />
		public bool MoveNext()
		{
			EnsureUnchanged();
			if (_index < _map._count) _index++;
			return _index < _map._count;
		}

		/// <inheritdoc />
		public void Reset() { EnsureUnchanged(); _index = -1; }
		/// <inheritdoc />
		public readonly void Dispose() { }

		private readonly void EnsureUnchanged()
		{
			if (_map is null || _version != _map._version)
				throw new InvalidOperationException("The collection changed or the enumerator is uninitialized.");
		}
	}

	private struct Slot
	{
		internal int DenseIndex;
		internal int NextFree;
		internal uint Generation;
	}
}

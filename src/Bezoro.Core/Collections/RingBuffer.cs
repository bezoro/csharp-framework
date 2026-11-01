using System.Collections;

namespace Bezoro.Core.Collections;

/// <summary>A fixed-capacity FIFO buffer with explicit rejection and overwrite operations.</summary>
/// <typeparam name="T">The stored value type.</typeparam>
/// <remarks>One thread must own mutations and reads, or callers must serialize access externally.</remarks>
public sealed class RingBuffer<T> : IReadOnlyList<T>
{
	private readonly T[] _values;
	private int _head;
	private int _count;
	private long _version;

	/// <summary>Creates an empty FIFO buffer with a positive, fixed capacity.</summary>
	/// <exception cref="ArgumentOutOfRangeException">Capacity is zero or negative.</exception>
	public RingBuffer(int capacity)
	{
		if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
		_values = new T[capacity];
	}

	/// <summary>Gets the live value count.</summary>
	public int Count => _count;
	/// <summary>Gets the fixed maximum value count.</summary>
	public int Capacity => _values.Length;
	/// <summary>Reports whether a rejecting enqueue would fail.</summary>
	public bool IsFull => _count == Capacity;

	/// <summary>Gets a value by logical FIFO position, where zero is the oldest value.</summary>
	/// <exception cref="ArgumentOutOfRangeException">The index is outside the live value range.</exception>
	public T this[int index]
	{
		get
		{
			if (unchecked((uint)index) >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
			return _values[PhysicalIndex(index)];
		}
	}

	/// <summary>Appends a value or returns false without mutation when full.</summary>
	public bool TryEnqueue(T value)
	{
		if (IsFull) return false;
		_values[PhysicalIndex(_count)] = value;
		_count++;
		_version = unchecked(_version + 1);
		return true;
	}

	/// <summary>Appends a value, throwing rather than overwriting when full.</summary>
	/// <exception cref="InvalidOperationException">The buffer is full.</exception>
	public void Enqueue(T value)
	{
		if (!TryEnqueue(value)) throw new InvalidOperationException("The ring buffer is full.");
	}

	/// <summary>Appends a value, evicting the oldest value only when full.</summary>
	/// <returns>True if a value was evicted. The output is default when there was no eviction.</returns>
	public bool EnqueueOverwrite(T value, out T overwritten)
	{
		if (!IsFull)
		{
			overwritten = default!;
			TryEnqueue(value);
			return false;
		}
		overwritten = _values[_head];
		_values[_head] = value;
		AdvanceHead();
		_version = unchecked(_version + 1);
		return true;
	}

	/// <summary>Removes the oldest value, clearing its storage, or returns false and default when empty.</summary>
	public bool TryDequeue(out T value)
	{
		if (_count == 0) { value = default!; return false; }
		value = _values[_head];
		_values[_head] = default!;
		_count--;
		if (_count == 0) _head = 0;
		else AdvanceHead();
		_version = unchecked(_version + 1);
		return true;
	}

	/// <summary>Removes the oldest value, throwing when empty.</summary>
	/// <exception cref="InvalidOperationException">The buffer is empty.</exception>
	public T Dequeue()
	{
		if (!TryDequeue(out T value)) throw new InvalidOperationException("The ring buffer is empty.");
		return value;
	}

	/// <summary>Copies the oldest value without removing it, or returns false and default when empty.</summary>
	public bool TryPeek(out T value)
	{
		if (_count == 0) { value = default!; return false; }
		value = _values[_head];
		return true;
	}

	/// <summary>Copies all live values in FIFO order without changing the buffer.</summary>
	/// <exception cref="ArgumentException">The destination is smaller than Count.</exception>
	public void CopyTo(Span<T> destination)
	{
		if (destination.Length < _count) throw new ArgumentException("The destination is smaller than the live value count.", nameof(destination));
		int first = Math.Min(_count, Capacity - _head);
		_values.AsSpan(_head, first).CopyTo(destination);
		_values.AsSpan(0, _count - first).CopyTo(destination.Slice(first));
	}

	/// <summary>Removes all values and releases references without disposing them.</summary>
	public void Clear()
	{
		if (_count == 0) return;
		int first = Math.Min(_count, Capacity - _head);
		Array.Clear(_values, _head, first);
		Array.Clear(_values, 0, _count - first);
		_head = 0;
		_count = 0;
		_version = unchecked(_version + 1);
	}

	private int PhysicalIndex(int logical)
	{
		int remaining = Capacity - _head;
		return logical < remaining ? _head + logical : logical - remaining;
	}

	private void AdvanceHead() => _head = _head + 1 == Capacity ? 0 : _head + 1;

	/// <summary>Returns an allocation-free FIFO-order enumerator.</summary>
	public Enumerator GetEnumerator() => new(this);
	IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	/// <summary>Enumerates FIFO values and rejects mutations after enumeration starts.</summary>
	public struct Enumerator : IEnumerator<T>
	{
		private readonly RingBuffer<T> _buffer;
		private readonly long _version;
		private int _index;
		internal Enumerator(RingBuffer<T> buffer) { _buffer = buffer; _version = buffer._version; _index = -1; }
		/// <inheritdoc />
		public readonly T Current
		{
			get
			{
				EnsureUnchanged();
				if (unchecked((uint)_index) >= (uint)_buffer._count) throw new InvalidOperationException("The enumerator is not positioned on a value.");
				return _buffer[_index];
			}
		}
		readonly object? IEnumerator.Current => Current;
		/// <inheritdoc />
		public bool MoveNext()
		{
			EnsureUnchanged();
			if (_index < _buffer._count) _index++;
			return _index < _buffer._count;
		}
		/// <inheritdoc />
		public void Reset() { EnsureUnchanged(); _index = -1; }
		/// <inheritdoc />
		public readonly void Dispose() { }
		private readonly void EnsureUnchanged()
		{
			if (_buffer is null || _version != _buffer._version)
				throw new InvalidOperationException("The collection changed or the enumerator is uninitialized.");
		}
	}
}

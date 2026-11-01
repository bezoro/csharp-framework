using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Bezoro.ECS.Internal;

internal sealed unsafe class ComponentColumn : IDisposable
{
	private const    int    ALIGNMENT_BYTES = 64;
	private readonly Array? _managedArray;
	private readonly bool   _isManaged;

	private readonly int    _elementSize;
	private readonly IntPtr _alignedPointer;
	private readonly IntPtr _allocatedPointer;
	private          bool   _disposed;

	private ComponentColumn(Type componentType, int capacity, bool isManaged)
	{
		ComponentType = componentType ?? throw new ArgumentNullException(nameof(componentType));
		if (capacity <= 0)
			throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");

		Capacity   = capacity;
		_isManaged = isManaged;

		if (isManaged)
		{
			_managedArray     = Array.CreateInstance(componentType, capacity);
			_elementSize      = 0;
			_alignedPointer   = IntPtr.Zero;
			_allocatedPointer = IntPtr.Zero;
		}
		else
		{
			_elementSize = Marshal.SizeOf(componentType);
			if (_elementSize <= 0)
				throw new InvalidOperationException("Component size must be positive.");

			var byteLength = checked((nuint)checked(_elementSize * capacity));
#if NET9_0_OR_GREATER
			var native = NativeMemory.AlignedAlloc(byteLength, ALIGNMENT_BYTES);
			if (native is null)
				throw new OutOfMemoryException("Failed to allocate unmanaged component buffer.");

			_allocatedPointer      = (IntPtr)native;
			_alignedPointer        = _allocatedPointer;
			UsesNativeAlignedAlloc = true;
#else
			var allocationLength = checked((nint)byteLength + ALIGNMENT_BYTES - 1);
			_allocatedPointer = Marshal.AllocHGlobal(allocationLength);
			if (_allocatedPointer == IntPtr.Zero)
				throw new OutOfMemoryException("Failed to allocate unmanaged component buffer.");

			var  baseAddress    = _allocatedPointer.ToInt64();
			long alignedAddress = baseAddress + (ALIGNMENT_BYTES - 1) & ~((long)ALIGNMENT_BYTES - 1);
			_alignedPointer        = new(alignedAddress);
			UsesNativeAlignedAlloc = false;
#endif

			new Span<byte>(_alignedPointer.ToPointer(), (int)byteLength).Clear();
			_managedArray = null;
		}
	}

	public static ComponentColumn Create(Type componentType, int capacity)
	{
		if (componentType is null) throw new ArgumentNullException(nameof(componentType));

		bool isManaged = !ComponentTypeTraits.IsUnmanaged(componentType);
		return new(componentType, capacity, isManaged);
	}

	~ComponentColumn()
	{
		Dispose(false);
	}

	public bool IsUnmanaged => !_isManaged;

	public int Capacity { get; }

	public Type ComponentType { get; }

	internal bool UsesNativeAlignedAlloc { get; }

	internal int AlignmentBytes => ALIGNMENT_BYTES;

	internal nuint AlignedAddress => (nuint)_alignedPointer.ToPointer();

	public object GetValue(int index)
	{
		ValidateIndex(index);
		if (_isManaged)
			return _managedArray!.GetValue(index)!;

		EnsureNotDisposed();
		return Marshal.PtrToStructure(new(GetPointer(index)), ComponentType)!;
	}

	public ReadOnlySpan<T> GetReadOnlySpan<T>(int length) where T : struct
	{
		ValidateType<T>();
		ValidateRange(0, length);
		if (_isManaged)
			return new((T[])_managedArray!, 0, length);

		EnsureNotDisposed();
		var bytes = new ReadOnlySpan<byte>(_alignedPointer.ToPointer(), checked(Capacity * _elementSize));
		return MemoryMarshal.Cast<byte, T>(bytes).Slice(0, length);
	}

	public Span<T> GetSpan<T>(int length) where T : struct
	{
		ValidateType<T>();
		ValidateRange(0, length);
		if (_isManaged)
			return new((T[])_managedArray!, 0, length);

		EnsureNotDisposed();
		var bytes = new Span<byte>(_alignedPointer.ToPointer(), checked(Capacity * _elementSize));
		return MemoryMarshal.Cast<byte, T>(bytes).Slice(0, length);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public ref T GetReference<T>(int index) where T : struct
	{
		if (_isManaged)
		{
			ValidateType<T>();
			ValidateIndex(index);
			return ref ((T[])_managedArray!)[index];
		}

		EnsureNotDisposed();
		ValidateType<T>();
		ValidateIndex(index);
		return ref Unsafe.Add(ref Unsafe.AsRef<T>(_alignedPointer.ToPointer()), index);
	}

	public void Clear(int index, int length)
	{
		ValidateRange(index, length);
		if (_isManaged)
		{
			Array.Clear(_managedArray!, index, length);
			return;
		}

		EnsureNotDisposed();
		int byteOffset = checked(index * _elementSize);
		int byteLength = checked(length * _elementSize);
		new Span<byte>((byte*)_alignedPointer.ToPointer() + byteOffset, byteLength).Clear();
	}

	public void CopyElementTo(int sourceIndex, ComponentColumn destination, int destinationIndex)
	{
		ValidateIndex(sourceIndex);
		if (destination is null) throw new ArgumentNullException(nameof(destination));
		destination.ValidateIndex(destinationIndex);

		if (!_isManaged && !destination._isManaged && destination.ComponentType == ComponentType)
		{
			EnsureNotDisposed();
			destination.EnsureNotDisposed();
			byte* source = GetPointer(sourceIndex);
			byte* target = destination.GetPointer(destinationIndex);
			Buffer.MemoryCopy(source, target, _elementSize, _elementSize);
			return;
		}

		if (_isManaged && destination._isManaged && destination.ComponentType == ComponentType)
		{
			Array.Copy(_managedArray!, sourceIndex, destination._managedArray!, destinationIndex, 1);
			return;
		}

		destination.SetValue(destinationIndex, GetValue(sourceIndex));
	}

	public void CopyRangeTo(
		int             sourceIndex,
		ComponentColumn destination,
		int             destinationIndex,
		int             length)
	{
		if (destination is null) throw new ArgumentNullException(nameof(destination));
		if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));

		ValidateRange(sourceIndex, length);
		destination.ValidateRange(destinationIndex, length);
		if (length == 0)
			return;

		if (!_isManaged && !destination._isManaged && destination.ComponentType == ComponentType)
		{
			EnsureNotDisposed();
			destination.EnsureNotDisposed();
			int byteLength = checked(_elementSize * length);
			int sourceOffset = checked(sourceIndex * _elementSize);
			int destinationOffset = checked(destinationIndex * destination._elementSize);
			var sourceBytes = new ReadOnlySpan<byte>((byte*)_alignedPointer.ToPointer() + sourceOffset, byteLength);
			var targetBytes = new Span<byte>(
				(byte*)destination._alignedPointer.ToPointer() + destinationOffset, byteLength
			);

			sourceBytes.CopyTo(targetBytes);
			return;
		}

		if (_isManaged && destination._isManaged && destination.ComponentType == ComponentType)
		{
			Array.Copy(_managedArray!, sourceIndex, destination._managedArray!, destinationIndex, length);
			return;
		}

		for (var i = 0; i < length; i++)
			destination.SetValue(destinationIndex + i, GetValue(sourceIndex + i));
	}

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	public void SetValue(int index, object value)
	{
		ValidateIndex(index);
		if (value is null) throw new ArgumentNullException(nameof(value));
		if (value.GetType() != ComponentType)
			throw new ArgumentException($"Expected value of type {ComponentType.FullName}.", nameof(value));

		if (_isManaged)
		{
			_managedArray!.SetValue(value, index);
			return;
		}

		EnsureNotDisposed();
		Marshal.StructureToPtr(value, new(GetPointer(index)), false);
	}

	private byte* GetPointer(int index) => (byte*)_alignedPointer.ToPointer() + index * _elementSize;

	private void Dispose(bool disposing)
	{
		if (_disposed) return;

		_disposed = true;
		if (_isManaged || _allocatedPointer == IntPtr.Zero)
			return;

#if NET9_0_OR_GREATER
		if (UsesNativeAlignedAlloc)
		{
			NativeMemory.AlignedFree(_allocatedPointer.ToPointer());
			return;
		}
#endif
		Marshal.FreeHGlobal(_allocatedPointer);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void EnsureNotDisposed()
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(ComponentColumn));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ValidateIndex(int index)
	{
		EnsureNotDisposed();
		if (unchecked((uint)index) >= unchecked((uint)Capacity))
			throw new ArgumentOutOfRangeException(nameof(index));
	}

	private void ValidateRange(int index, int length)
	{
		EnsureNotDisposed();
		if (length < 0)
			throw new ArgumentOutOfRangeException(nameof(length));

		if (unchecked((uint)index) > unchecked((uint)Capacity) || length > Capacity - index)
			throw new ArgumentOutOfRangeException(nameof(index));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ValidateType<T>() where T : struct
	{
		if (typeof(T) != ComponentType)
			throw new InvalidOperationException(
				$"Column type mismatch. Expected {ComponentType.FullName}, got {typeof(T).FullName}."
			);
	}
}

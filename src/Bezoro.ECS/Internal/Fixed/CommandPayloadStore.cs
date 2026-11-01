using System.Buffers;
using System.Runtime.CompilerServices;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Internal.Fixed;

internal sealed class CommandPayloadStore<T>(int capacity, WorldOverflowPolicy overflowPolicy)
	: ICommandPayloadStore
	where T : struct
{
	private readonly bool _containsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
	private readonly int  _capacity           = capacity;
	private readonly T[]  _payloads           = ArrayPool<T>.Shared.Rent(capacity);
	private          bool _disposed;
	private          int  _count;

	public bool IsFull => _count >= _capacity;

	public bool TryAdd(in T payload, out int payloadIndex)
	{
		ThrowIfDisposed();

		if (_count >= _capacity)
		{
			if (overflowPolicy == WorldOverflowPolicy.FailFast)
				throw new InvalidOperationException(
					$"Command payload capacity of {_capacity} was exceeded for '{typeof(T).Name}'."
				);

			payloadIndex = -1;
			return false;
		}

		payloadIndex      = _count;
		_payloads[_count] = payload;
		_count++;
		return true;
	}

	public void Apply(World world, Entity entity, int payloadIndex)
	{
		ThrowIfDisposed();

		if (unchecked((uint)payloadIndex) >= unchecked((uint)_count))
			throw new InvalidOperationException(
				$"Payload index '{payloadIndex}' is out of range for '{typeof(T).Name}'."
			);

		world.ApplySetFromCommand(entity, in _payloads[payloadIndex]);
	}

	public void ApplyBatch(
		World world,
		int[] entityIds,
		int   entityOffset,
		int   count,
		int[] payloadIndices,
		int   payloadOffset,
		int   componentTypeId,
		int   sourceArchetypeId,
		int   targetArchetypeId
	)
	{
		ThrowIfDisposed();
		if (world is null) throw new ArgumentNullException(nameof(world));
		if (entityIds is null) throw new ArgumentNullException(nameof(entityIds));
		if (payloadIndices is null) throw new ArgumentNullException(nameof(payloadIndices));
		if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
		if (entityOffset < 0 || entityOffset + count > entityIds.Length)
			throw new ArgumentOutOfRangeException(nameof(entityOffset));

		if (payloadOffset < 0 || payloadOffset + count > payloadIndices.Length)
			throw new ArgumentOutOfRangeException(nameof(payloadOffset));

		world.ApplySetBatchFromCommandKnownTransitionFast(
			entityIds,
			entityOffset,
			count,
			_payloads,
			payloadIndices,
			payloadOffset,
			componentTypeId,
			sourceArchetypeId,
			targetArchetypeId
		);
	}

	public void Clear()
	{
		ThrowIfDisposed();

		if (_containsReferences && _count > 0)
			Array.Clear(_payloads, 0, _count);

		_count = 0;
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		if (_containsReferences)
			Array.Clear(_payloads, 0, _payloads.Length);

		ArrayPool<T>.Shared.Return(_payloads);
		_disposed = true;
		_count    = 0;
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(CommandPayloadStore<T>));
	}
}

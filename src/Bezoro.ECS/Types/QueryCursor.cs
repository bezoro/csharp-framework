using System.Runtime.CompilerServices;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Internal;
using Bezoro.ECS.Internal.Fixed;
using Bezoro.ECS.Services;

namespace Bezoro.ECS.Types;

/// <summary>
///     Cursor over one compiled-query execution result set.
/// </summary>
/// <remarks>
///     Copies share one execution lifetime. Disposing any copy invalidates all copies.
///     Returned spans and component references are borrowed until disposal.
///     Callback and job entry atomically pins that lifetime against concurrent disposal of another copy.
///     Component access and borrowed references still require the caller's access discipline.
/// </remarks>
public struct QueryCursor : IDisposable
{
	private const int MISSING_COLUMN_INDEX           = -1;
	private const int UNINITIALIZED_CHUNK_MATCH_HINT = -1;
	private const int UNKNOWN_ARCHETYPE_ID           = int.MinValue;
	private const int UNKNOWN_TYPE_ID                = int.MinValue;

	private readonly Entity[]            _entities;
	private readonly int                 _chunkMatchCount;
	private readonly int                 _count;
	private readonly QueryChunkMatch[]   _chunkMatches;
	private readonly QueryExecutionLease _lease;
	private readonly long                _leaseGeneration;

	private readonly World             _world;
	private          ArchetypeStorage? _cachedGetArchetype;
	private          ArchetypeStorage? _cachedReadArchetype;
	private          bool              _disposed;
	private          bool              _entitiesMaterialized;
	private          bool              _moved;
	private          int               _cachedGetArchetypeId;
	private          int               _cachedGetColumnIndex;
	private          int               _cachedGetTypeId;
	private          int               _cachedReadArchetypeId;
	private          int               _cachedReadColumnIndex;
	private          int               _cachedReadTypeId;
	private          int               _chunkMatchHint;

	/// <summary>
	///     Delegate invoked with one entity and one mutable unmanaged component.
	/// </summary>
	public delegate void EntityRefAction<T1>(Entity entity, ref T1 component1) where T1 : unmanaged;

	/// <summary>
	///     Delegate invoked with one entity, one mutable, and three read-only unmanaged components.
	/// </summary>
	public delegate void EntityRefInAction<T1, T2, T3, T4>(
		Entity entity,
		ref T1 component1,
		in  T2 component2,
		in  T3 component3,
		in  T4 component4)
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged;

	/// <summary>
	///     Delegate invoked with one entity, one mutable, and two read-only unmanaged components.
	/// </summary>
	public delegate void EntityRefInAction<T1, T2, T3>(
		Entity entity,
		ref T1 component1,
		in  T2 component2,
		in  T3 component3)
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged;

	/// <summary>
	///     Delegate invoked with one entity, one mutable, and one read-only unmanaged component.
	/// </summary>
	public delegate void EntityRefInAction<T1, T2>(Entity entity, ref T1 component1, in T2 component2)
		where T1 : unmanaged
		where T2 : unmanaged;

	/// <summary>
	///     Delegate invoked with one mutable unmanaged component.
	/// </summary>
	public delegate void RefAction<T1>(ref T1 component1) where T1 : unmanaged;

	/// <summary>
	///     Delegate invoked with one mutable and three read-only unmanaged components.
	/// </summary>
	public delegate void RefInAction<T1, T2, T3, T4>(
		ref T1 component1,
		in  T2 component2,
		in  T3 component3,
		in  T4 component4)
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged;

	/// <summary>
	///     Delegate invoked with one mutable and two read-only unmanaged components.
	/// </summary>
	public delegate void RefInAction<T1, T2, T3>(ref T1 component1, in T2 component2, in T3 component3)
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged;

	/// <summary>
	///     Delegate invoked with one mutable and one read-only unmanaged component.
	/// </summary>
	public delegate void RefInAction<T1, T2>(ref T1 component1, in T2 component2)
		where T1 : unmanaged
		where T2 : unmanaged;

	internal QueryCursor(
		World               world,
		QueryChunkMatch[]   chunkMatches,
		int                 chunkMatchCount,
		Entity[]            entities,
		int                 count,
		QueryExecutionLease lease
	)
	{
		_world                 = world;
		_chunkMatches          = chunkMatches;
		_chunkMatchCount       = chunkMatchCount;
		_entities              = entities;
		_count                 = count;
		_lease                 = lease;
		_leaseGeneration       = lease.Generation;
		_disposed              = false;
		_moved                 = false;
		_entitiesMaterialized  = false;
		_chunkMatchHint        = UNINITIALIZED_CHUNK_MATCH_HINT;
		_cachedGetTypeId       = UNKNOWN_TYPE_ID;
		_cachedGetArchetypeId  = UNKNOWN_ARCHETYPE_ID;
		_cachedGetColumnIndex  = MISSING_COLUMN_INDEX;
		_cachedGetArchetype    = null;
		_cachedReadTypeId      = UNKNOWN_TYPE_ID;
		_cachedReadArchetypeId = UNKNOWN_ARCHETYPE_ID;
		_cachedReadColumnIndex = MISSING_COLUMN_INDEX;
		_cachedReadArchetype   = null;
	}

	/// <summary>
	///     Gets the number of matched entities without materializing entity handles.
	/// </summary>
	public int Count
	{
		get { ThrowIfDisposed(); return _count; }
	}

	/// <summary>
	///     Current matched entities.
	/// </summary>
	public ReadOnlySpan<Entity> Current
	{
		get
		{
			ThrowIfDisposed();
			if (!_moved)
				return ReadOnlySpan<Entity>.Empty;

			EnsureEntitiesMaterialized();
			return new(_entities, 0, _count);
		}
	}

	/// <summary>
	///     Unlocks access to the single result batch and signals that the cursor is ready to use.
	///     This cursor exposes exactly one batch; subsequent calls return <c>false</c> without advancing.
	/// </summary>
	/// <returns><c>true</c> on the first call; <c>false</c> on all subsequent calls.</returns>
	public bool MoveNext()
	{
		ThrowIfDisposed();
		if (_moved)
			return false;

		_moved = true;
		return true;
	}

	/// <summary>
	///     Gets one matched entity without materializing the complete entity array.
	/// </summary>
	/// <param name="index">Zero-based entity index in the current batch.</param>
	/// <returns>The entity at <paramref name="index" />.</returns>
	/// <exception cref="InvalidOperationException">Call <see cref="MoveNext" /> before reading the batch.</exception>
	/// <exception cref="ArgumentOutOfRangeException">The index is outside the result set.</exception>
	public Entity GetEntity(int index)
	{
		ValidateReadyForEnumeration();
		if (unchecked((uint)index) >= unchecked((uint)_count) ||
			!TryResolveChunkMatchForIndex(index, out var match, out int offset))
			throw new ArgumentOutOfRangeException(nameof(index));
		var chunk = _world.GetArchetypeForCursor(match.ArchetypeId).GetChunkUnchecked(match.ChunkIndex);
		return _world.GetEntityForCursor(chunk.EntityIds[match.RowStart + offset]);
	}

	/// <summary>
	///     Gets a mutable unmanaged component reference for the matched entity at <paramref name="index" />.
	/// </summary>
	/// <typeparam name="T">Unmanaged component type.</typeparam>
	/// <param name="index">Entity index in the current batch.</param>
	/// <returns>Mutable component reference.</returns>
	public ref T Write<T>(int index) where T : unmanaged
	{
		ThrowIfDisposed();
		if (!_moved)
			throw new InvalidOperationException("Call MoveNext before accessing components.");

		if (unchecked((uint)index) >= unchecked((uint)_count))
			throw new ArgumentOutOfRangeException(nameof(index));

		int typeId = _world.GetOrCreateComponentTypeId<T>();
		if (typeId != _cachedGetTypeId)
		{
			_cachedGetTypeId      = typeId;
			_cachedGetArchetypeId = UNKNOWN_ARCHETYPE_ID;
			_cachedGetColumnIndex = MISSING_COLUMN_INDEX;
			_cachedGetArchetype   = null;
		}

		if (!TryResolveChunkMatchForIndex(index, out var match, out int matchOffset))
			throw new ArgumentOutOfRangeException(nameof(index));

		if (match.ArchetypeId != _cachedGetArchetypeId)
		{
			var archetype   = _world.GetArchetypeForCursor(match.ArchetypeId);
			int columnIndex = archetype.GetColumnIndexOrNegative(typeId);
			if (columnIndex < 0)
				throw new KeyNotFoundException(
					$"Type id '{typeId}' does not exist in archetype '{match.ArchetypeId}'."
				);

			_cachedGetArchetypeId = match.ArchetypeId;
			_cachedGetColumnIndex = columnIndex;
			_cachedGetArchetype   = archetype;
		}

		var cachedArchetype = _cachedGetArchetype!;
		var chunk           = cachedArchetype.GetChunkUnchecked(match.ChunkIndex);
		_world.TrackPotentialCursorRefWrite(chunk, _cachedGetColumnIndex, typeId, match.RowStart + matchOffset);
		return ref cachedArchetype.GetRefByIndex<T>(
				   chunk,
				   _cachedGetColumnIndex,
				   match.RowStart + matchOffset
			   );
	}

	/// <summary>
	///     Gets a read-only unmanaged component reference for the matched entity at <paramref name="index" />.
	/// </summary>
	/// <typeparam name="T">Unmanaged component type.</typeparam>
	/// <param name="index">Entity index in the current batch.</param>
	/// <returns>Read-only component reference.</returns>
	public ref readonly T Read<T>(int index) where T : unmanaged
	{
		ThrowIfDisposed();
		if (!_moved)
			throw new InvalidOperationException("Call MoveNext before accessing components.");

		if (unchecked((uint)index) >= unchecked((uint)_count))
			throw new ArgumentOutOfRangeException(nameof(index));

		int typeId = _world.GetOrCreateComponentTypeId<T>();
		if (typeId != _cachedReadTypeId)
		{
			_cachedReadTypeId      = typeId;
			_cachedReadArchetypeId = UNKNOWN_ARCHETYPE_ID;
			_cachedReadColumnIndex = MISSING_COLUMN_INDEX;
			_cachedReadArchetype   = null;
		}

		if (!TryResolveChunkMatchForIndex(index, out var match, out int matchOffset))
			throw new ArgumentOutOfRangeException(nameof(index));

		if (match.ArchetypeId != _cachedReadArchetypeId)
		{
			var archetype   = _world.GetArchetypeForCursor(match.ArchetypeId);
			int columnIndex = archetype.GetColumnIndexOrNegative(typeId);
			if (columnIndex < 0)
				throw new KeyNotFoundException(
					$"Type id '{typeId}' does not exist in archetype '{match.ArchetypeId}'."
				);

			_cachedReadArchetypeId = match.ArchetypeId;
			_cachedReadColumnIndex = columnIndex;
			_cachedReadArchetype   = archetype;
		}

		var cachedArchetype = _cachedReadArchetype!;
		var chunk           = cachedArchetype.GetChunkUnchecked(match.ChunkIndex);
		return ref cachedArchetype.GetRefByIndex<T>(
				   chunk,
				   _cachedReadColumnIndex,
				   match.RowStart + matchOffset
			   );
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
			return;

		_lease?.Dispose(_leaseGeneration);
		_disposed = true;
	}

	/// <summary>
	///     Executes a no-allocation sequential loop over one mutable unmanaged component.
	/// </summary>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <param name="action">Per-entity callback.</param>
	public void ForEach<T1>(RefAction<T1> action) where T1 : unmanaged
	{
		if (action is null) throw new ArgumentNullException(nameof(action));

		ValidateReadyForEnumeration();
		using var iteration = _lease.EnterIteration(_leaseGeneration);
		QueryChunkWalker.Execute<DelegateChunkAction<T1>, T1>(_world, _chunkMatches, _chunkMatchCount, new(action));
	}

	/// <summary>
	///     Executes a no-allocation sequential loop over one mutable and one read-only unmanaged component.
	/// </summary>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">Read-only unmanaged component type.</typeparam>
	/// <param name="action">Per-entity callback.</param>
	public void ForEach<T1, T2>(RefInAction<T1, T2> action)
		where T1 : unmanaged
		where T2 : unmanaged
	{
		if (action is null) throw new ArgumentNullException(nameof(action));

		ValidateReadyForEnumeration();
		using var iteration = _lease.EnterIteration(_leaseGeneration);
		QueryChunkWalker.Execute<DelegateChunkAction<T1, T2>, T1, T2>(
			_world, _chunkMatches, _chunkMatchCount, new(action)
		);
	}

	/// <summary>
	///     Executes a no-allocation sequential loop over one mutable and two read-only unmanaged components.
	/// </summary>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">First read-only unmanaged component type.</typeparam>
	/// <typeparam name="T3">Second read-only unmanaged component type.</typeparam>
	/// <param name="action">Per-entity callback.</param>
	public void ForEach<T1, T2, T3>(RefInAction<T1, T2, T3> action)
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
	{
		if (action is null) throw new ArgumentNullException(nameof(action));

		ValidateReadyForEnumeration();
		using var iteration = _lease.EnterIteration(_leaseGeneration);
		QueryChunkWalker.Execute<DelegateChunkAction<T1, T2, T3>, T1, T2, T3>(
			_world, _chunkMatches, _chunkMatchCount, new(action)
		);
	}

	/// <summary>
	///     Executes a no-allocation sequential loop over one mutable and three read-only unmanaged components.
	/// </summary>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">First read-only unmanaged component type.</typeparam>
	/// <typeparam name="T3">Second read-only unmanaged component type.</typeparam>
	/// <typeparam name="T4">Third read-only unmanaged component type.</typeparam>
	/// <param name="action">Per-entity callback.</param>
	public void ForEach<T1, T2, T3, T4>(RefInAction<T1, T2, T3, T4> action)
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged
	{
		if (action is null) throw new ArgumentNullException(nameof(action));

		ValidateReadyForEnumeration();
		using var iteration = _lease.EnterIteration(_leaseGeneration);
		QueryChunkWalker.Execute<DelegateChunkAction<T1, T2, T3, T4>, T1, T2, T3, T4>(
			_world,
			_chunkMatches,
			_chunkMatchCount,
			new(action)
		);
	}

	/// <summary>
	///     Executes a no-allocation sequential entity-aware loop over one mutable unmanaged component.
	/// </summary>
	public void ForEachEntity<T1>(EntityRefAction<T1> action)
		where T1 : unmanaged
	{
		ThrowIfDisposed();
		if (action is null) throw new ArgumentNullException(nameof(action));
		if (!_moved)
			throw new InvalidOperationException("Call MoveNext before enumerating components.");

		ExecuteEntityAction<DelegateEntityAction<T1>, T1>(new(action));
	}

	/// <summary>
	///     Executes a no-allocation sequential entity-aware loop over one mutable and one read-only unmanaged component.
	/// </summary>
	public void ForEachEntity<T1, T2>(EntityRefInAction<T1, T2> action)
		where T1 : unmanaged
		where T2 : unmanaged
	{
		ThrowIfDisposed();
		if (action is null) throw new ArgumentNullException(nameof(action));
		if (!_moved)
			throw new InvalidOperationException("Call MoveNext before enumerating components.");

		ExecuteEntityAction<DelegateEntityAction<T1, T2>, T1, T2>(new(action));
	}

	/// <summary>
	///     Executes a no-allocation sequential entity-aware loop over one mutable and two read-only unmanaged components.
	/// </summary>
	public void ForEachEntity<T1, T2, T3>(EntityRefInAction<T1, T2, T3> action)
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
	{
		ThrowIfDisposed();
		if (action is null) throw new ArgumentNullException(nameof(action));
		if (!_moved)
			throw new InvalidOperationException("Call MoveNext before enumerating components.");

		ExecuteEntityAction<DelegateEntityAction<T1, T2, T3>, T1, T2, T3>(new(action));
	}

	/// <summary>
	///     Executes a no-allocation sequential entity-aware loop over one mutable and three read-only unmanaged components.
	/// </summary>
	public void ForEachEntity<T1, T2, T3, T4>(EntityRefInAction<T1, T2, T3, T4> action)
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged
	{
		ThrowIfDisposed();
		if (action is null) throw new ArgumentNullException(nameof(action));
		if (!_moved)
			throw new InvalidOperationException("Call MoveNext before enumerating components.");

		ExecuteEntityAction<DelegateEntityAction<T1, T2, T3, T4>, T1, T2, T3, T4>(new(action));
	}

	/// <summary>
	///     Executes a no-allocation sequential struct job over one mutable unmanaged component.
	/// </summary>
	/// <typeparam name="TJob">Job type implementing <see cref="IForEach{T1}" />.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	public void Run<TJob, T1>(TJob job)
		where TJob : struct, IForEach<T1>
		where T1 : unmanaged
	{
		ValidateReadyForEnumeration();
		using var iteration = _lease.EnterIteration(_leaseGeneration);
		QueryChunkWalker.Execute<JobChunkAction<TJob, T1>, T1>(_world, _chunkMatches, _chunkMatchCount, new(job));
	}

	/// <summary>
	///     Executes a no-allocation sequential struct job over one mutable and one read-only unmanaged component.
	/// </summary>
	/// <typeparam name="TJob">Job type implementing <see cref="IForEach{T1, T2}" />.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">Read-only unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	public void Run<TJob, T1, T2>(TJob job)
		where TJob : struct, IForEach<T1, T2>
		where T1 : unmanaged
		where T2 : unmanaged
	{
		ValidateReadyForEnumeration();
		using var iteration = _lease.EnterIteration(_leaseGeneration);
		QueryChunkWalker.Execute<JobChunkAction<TJob, T1, T2>, T1, T2>(
			_world, _chunkMatches, _chunkMatchCount, new(job)
		);
	}

	/// <summary>
	///     Executes a no-allocation sequential struct job over one mutable and two read-only unmanaged components.
	/// </summary>
	/// <typeparam name="TJob">Job type implementing <see cref="IForEach{T1, T2, T3}" />.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">First read-only unmanaged component type.</typeparam>
	/// <typeparam name="T3">Second read-only unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	public void Run<TJob, T1, T2, T3>(TJob job)
		where TJob : struct, IForEach<T1, T2, T3>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
	{
		ValidateReadyForEnumeration();
		using var iteration = _lease.EnterIteration(_leaseGeneration);
		QueryChunkWalker.Execute<JobChunkAction<TJob, T1, T2, T3>, T1, T2, T3>(
			_world,
			_chunkMatches,
			_chunkMatchCount,
			new(job)
		);
	}

	/// <summary>
	///     Executes a no-allocation sequential struct job over one mutable and three read-only unmanaged components.
	/// </summary>
	/// <typeparam name="TJob">Job type implementing <see cref="IForEach{T1, T2, T3, T4}" />.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">First read-only unmanaged component type.</typeparam>
	/// <typeparam name="T3">Second read-only unmanaged component type.</typeparam>
	/// <typeparam name="T4">Third read-only unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	public void Run<TJob, T1, T2, T3, T4>(TJob job)
		where TJob : struct, IForEach<T1, T2, T3, T4>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged
	{
		ValidateReadyForEnumeration();
		using var iteration = _lease.EnterIteration(_leaseGeneration);
		QueryChunkWalker.Execute<JobChunkAction<TJob, T1, T2, T3, T4>, T1, T2, T3, T4>(
			_world,
			_chunkMatches,
			_chunkMatchCount,
			new(job)
		);
	}

	/// <summary>
	///     Executes a no-allocation sequential entity-aware struct job over one mutable unmanaged component.
	/// </summary>
	public void RunEntity<TJob, T1>(TJob job)
		where TJob : struct, IForEachEntity<T1>
		where T1 : unmanaged
	{
		ThrowIfDisposed();
		if (!_moved)
			throw new InvalidOperationException("Call MoveNext before enumerating components.");

		ExecuteEntityAction<JobEntityAction<TJob, T1>, T1>(new(job));
	}

	/// <summary>
	///     Executes a no-allocation sequential entity-aware struct job over one mutable and one read-only unmanaged component.
	/// </summary>
	public void RunEntity<TJob, T1, T2>(TJob job)
		where TJob : struct, IForEachEntity<T1, T2>
		where T1 : unmanaged
		where T2 : unmanaged
	{
		ThrowIfDisposed();
		if (!_moved)
			throw new InvalidOperationException("Call MoveNext before enumerating components.");

		ExecuteEntityAction<JobEntityAction<TJob, T1, T2>, T1, T2>(new(job));
	}

	/// <summary>
	///     Executes a no-allocation sequential entity-aware struct job over one mutable and two read-only unmanaged
	///     components.
	/// </summary>
	public void RunEntity<TJob, T1, T2, T3>(TJob job)
		where TJob : struct, IForEachEntity<T1, T2, T3>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
	{
		ThrowIfDisposed();
		if (!_moved)
			throw new InvalidOperationException("Call MoveNext before enumerating components.");

		ExecuteEntityAction<JobEntityAction<TJob, T1, T2, T3>, T1, T2, T3>(new(job));
	}

	/// <summary>
	///     Executes a no-allocation sequential entity-aware struct job over one mutable and three read-only unmanaged
	///     components.
	/// </summary>
	public void RunEntity<TJob, T1, T2, T3, T4>(TJob job)
		where TJob : struct, IForEachEntity<T1, T2, T3, T4>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged
	{
		ThrowIfDisposed();
		if (!_moved)
			throw new InvalidOperationException("Call MoveNext before enumerating components.");

		ExecuteEntityAction<JobEntityAction<TJob, T1, T2, T3, T4>, T1, T2, T3, T4>(new(job));
	}

	internal void ExecuteEntityAction<TAction, T1>(TAction action)
		where TAction : struct, IEntityChunkAction<T1>
		where T1 : struct
	{
		ValidateReadyForEnumeration();
		using var iteration = _lease.EnterIteration(_leaseGeneration);
		QueryChunkWalker.ExecuteEntity<TAction, T1>(
			_world,
			_chunkMatches,
			_chunkMatchCount,
			action,
			trackWrites: true
		);
	}

	internal void ExecuteEntityAction<TAction, T1, T2>(TAction action)
		where TAction : struct, IEntityChunkAction<T1, T2>
		where T1 : struct
		where T2 : struct
	{
		ValidateReadyForEnumeration();
		using var iteration = _lease.EnterIteration(_leaseGeneration);
		QueryChunkWalker.ExecuteEntity<TAction, T1, T2>(_world, _chunkMatches, _chunkMatchCount, action);
	}

	internal void ExecuteEntityAction<TAction, T1, T2, T3>(TAction action)
		where TAction : struct, IEntityChunkAction<T1, T2, T3>
		where T1 : struct
		where T2 : struct
		where T3 : struct
	{
		ValidateReadyForEnumeration();
		using var iteration = _lease.EnterIteration(_leaseGeneration);
		QueryChunkWalker.ExecuteEntity<TAction, T1, T2, T3>(_world, _chunkMatches, _chunkMatchCount, action);
	}

	internal void ExecuteEntityAction<TAction, T1, T2, T3, T4>(TAction action)
		where TAction : struct, IEntityChunkAction<T1, T2, T3, T4>
		where T1 : struct
		where T2 : struct
		where T3 : struct
		where T4 : struct
	{
		ValidateReadyForEnumeration();
		using var iteration = _lease.EnterIteration(_leaseGeneration);
		QueryChunkWalker.ExecuteEntity<TAction, T1, T2, T3, T4>(_world, _chunkMatches, _chunkMatchCount, action);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool TryResolveMatchOffset(in QueryChunkMatch match, int index, out int matchOffset)
	{
		matchOffset = index - match.EntityStartIndex;
		return unchecked((uint)matchOffset) < unchecked((uint)match.Count);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryResolveChunkMatchForIndex(int index, out QueryChunkMatch match, out int matchOffset)
	{
		if (unchecked((uint)_chunkMatchHint) < unchecked((uint)_chunkMatchCount))
		{
			var hintedMatch = _chunkMatches[_chunkMatchHint];
			if (TryResolveMatchOffset(hintedMatch, index, out matchOffset))
			{
				match = hintedMatch;
				return true;
			}

			int nextHint = _chunkMatchHint + 1;
			if (unchecked((uint)nextHint) < unchecked((uint)_chunkMatchCount))
			{
				var nextMatch = _chunkMatches[nextHint];
				if (TryResolveMatchOffset(nextMatch, index, out matchOffset))
				{
					_chunkMatchHint = nextHint;
					match           = nextMatch;
					return true;
				}
			}

			int previousHint = _chunkMatchHint - 1;
			if (previousHint >= 0)
			{
				var previousMatch = _chunkMatches[previousHint];
				if (TryResolveMatchOffset(previousMatch, index, out matchOffset))
				{
					_chunkMatchHint = previousHint;
					match           = previousMatch;
					return true;
				}
			}
		}

		var low  = 0;
		int high = _chunkMatchCount - 1;
		while (low <= high)
		{
			int middle      = low + (high - low >> 1);
			var middleMatch = _chunkMatches[middle];
			int start       = middleMatch.EntityStartIndex;
			int offset      = index - start;
			if (offset < 0)
			{
				high = middle - 1;
				continue;
			}

			if (unchecked((uint)offset) >= unchecked((uint)middleMatch.Count))
			{
				low = middle + 1;
				continue;
			}

			_chunkMatchHint = middle;
			match           = middleMatch;
			matchOffset     = offset;
			return true;
		}

		match       = default;
		matchOffset = default;
		return false;
	}

	private void EnsureEntitiesMaterialized()
	{
		if (_entitiesMaterialized)
			return;

		_world.MaterializeQueryEntities(_chunkMatches, _chunkMatchCount, _entities, _count);
		_entitiesMaterialized = true;
	}

	private void ThrowIfDisposed()
	{
		if (_disposed || _lease is null || !_lease.IsActive(_leaseGeneration))
			throw new ObjectDisposedException(nameof(QueryCursor));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ValidateReadyForEnumeration()
	{
		ThrowIfDisposed();
		if (!_moved)
			throw new InvalidOperationException("Call MoveNext before enumerating components.");
	}

	private readonly struct DelegateChunkAction<T1>(RefAction<T1> action) : IChunkAction<T1>
		where T1 : unmanaged
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Invoke(ref T1 component1) => action(ref component1);
	}

	private readonly struct DelegateChunkAction<T1, T2>(RefInAction<T1, T2> action) : IChunkAction<T1, T2>
		where T1 : unmanaged
		where T2 : unmanaged
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Invoke(ref T1 component1, in T2 component2) => action(ref component1, in component2);
	}

	private readonly struct DelegateChunkAction<T1, T2, T3>(RefInAction<T1, T2, T3> action) : IChunkAction<T1, T2, T3>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Invoke(ref T1 component1, in T2 component2, in T3 component3) =>
			action(ref component1, in component2, in component3);
	}

	private readonly struct DelegateChunkAction<T1, T2, T3, T4>(RefInAction<T1, T2, T3, T4> action)
		: IChunkAction<T1, T2, T3, T4>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Invoke(ref T1 component1, in T2 component2, in T3 component3, in T4 component4) =>
			action(ref component1, in component2, in component3, in component4);
	}

	private readonly struct DelegateEntityAction<T1>(EntityRefAction<T1> action) : IEntityChunkAction<T1>
		where T1 : unmanaged
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Invoke(Entity entity, ref T1 component1) => action(entity, ref component1);
	}

	private readonly struct DelegateEntityAction<T1, T2>(EntityRefInAction<T1, T2> action) : IEntityChunkAction<T1, T2>
		where T1 : unmanaged
		where T2 : unmanaged
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Invoke(Entity entity, ref T1 component1, in T2 component2) =>
			action(entity, ref component1, in component2);
	}

	private readonly struct DelegateEntityAction<T1, T2, T3>(EntityRefInAction<T1, T2, T3> action)
		: IEntityChunkAction<T1, T2, T3>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Invoke(Entity entity, ref T1 component1, in T2 component2, in T3 component3) => action(
			entity, ref component1, in component2, in component3
		);
	}

	private readonly struct DelegateEntityAction<T1, T2, T3, T4>(EntityRefInAction<T1, T2, T3, T4> action)
		: IEntityChunkAction<T1, T2, T3, T4>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Invoke(Entity entity, ref T1 component1, in T2 component2, in T3 component3, in T4 component4) =>
			action(entity, ref component1, in component2, in component3, in component4);
	}

	private struct JobChunkAction<TJob, T1>(TJob job) : IChunkAction<T1>
		where TJob : struct, IForEach<T1>
		where T1 : unmanaged
	{
		private TJob _job = job;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Invoke(ref T1 component1) => _job.Execute(ref component1);
	}

	private struct JobChunkAction<TJob, T1, T2>(TJob job) : IChunkAction<T1, T2>
		where TJob : struct, IForEach<T1, T2>
		where T1 : unmanaged
		where T2 : unmanaged
	{
		private TJob _job = job;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Invoke(ref T1 component1, in T2 component2) => _job.Execute(ref component1, in component2);
	}

	private struct JobChunkAction<TJob, T1, T2, T3>(TJob job) : IChunkAction<T1, T2, T3>
		where TJob : struct, IForEach<T1, T2, T3>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
	{
		private TJob _job = job;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Invoke(ref T1 component1, in T2 component2, in T3 component3) =>
			_job.Execute(ref component1, in component2, in component3);
	}

	private struct JobChunkAction<TJob, T1, T2, T3, T4>(TJob job) : IChunkAction<T1, T2, T3, T4>
		where TJob : struct, IForEach<T1, T2, T3, T4>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged
	{
		private TJob _job = job;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Invoke(ref T1 component1, in T2 component2, in T3 component3, in T4 component4) =>
			_job.Execute(ref component1, in component2, in component3, in component4);
	}

	private struct JobEntityAction<TJob, T1>(TJob job) : IEntityChunkAction<T1>
		where TJob : struct, IForEachEntity<T1>
		where T1 : unmanaged
	{
		private TJob _job = job;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Invoke(Entity entity, ref T1 component1) => _job.Execute(entity, ref component1);
	}

	private struct JobEntityAction<TJob, T1, T2>(TJob job) : IEntityChunkAction<T1, T2>
		where TJob : struct, IForEachEntity<T1, T2>
		where T1 : unmanaged
		where T2 : unmanaged
	{
		private TJob _job = job;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Invoke(Entity entity, ref T1 component1, in T2 component2) =>
			_job.Execute(entity, ref component1, in component2);
	}

	private struct JobEntityAction<TJob, T1, T2, T3>(TJob job) : IEntityChunkAction<T1, T2, T3>
		where TJob : struct, IForEachEntity<T1, T2, T3>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
	{
		private TJob _job = job;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Invoke(Entity entity, ref T1 component1, in T2 component2, in T3 component3) =>
			_job.Execute(entity, ref component1, in component2, in component3);
	}

	private struct JobEntityAction<TJob, T1, T2, T3, T4>(TJob job) : IEntityChunkAction<T1, T2, T3, T4>
		where TJob : struct, IForEachEntity<T1, T2, T3, T4>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged
	{
		private TJob _job = job;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Invoke(Entity entity, ref T1 component1, in T2 component2, in T3 component3, in T4 component4) =>
			_job.Execute(entity, ref component1, in component2, in component3, in component4);
	}
}

using Bezoro.ECS.Internal.Fixed;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Internal;

internal sealed class QueryExecutionLease
{
	private bool               _usesSharedScratch;
	private Entity[]?          _entities;
	// Odd generations own an execution; the following even generation is disposed.
	// A cursor copy retains its generation even when this lease returns to the pool.
	private long               _generation;
	private int                _activeIterations;
	private QueryChunkMatch[]? _chunkMatches;
	private World?             _world;

	internal void Initialize(
		World             world,
		QueryChunkMatch[] chunkMatches,
		Entity[]          entities,
		bool              usesSharedScratch)
	{
		_world             = world;
		_chunkMatches      = chunkMatches;
		_entities          = entities;
		_usesSharedScratch = usesSharedScratch;
		Interlocked.Increment(ref _generation);
	}

	internal long Generation => Volatile.Read(ref _generation);

	internal bool IsActive(long generation) => Volatile.Read(ref _generation) == generation;

	internal IterationScope EnterIteration(long generation)
	{
		if (!IsActive(generation)) throw new ObjectDisposedException(nameof(QueryCursor));
		Interlocked.Increment(ref _activeIterations);
		return new(this);
	}

	internal void Dispose(long generation)
	{
		if (!IsActive(generation)) return;
		if (Volatile.Read(ref _activeIterations) != 0)
			throw new InvalidOperationException("A cursor cannot be disposed while its callback or job is running.");
		if (Interlocked.CompareExchange(ref _generation, unchecked(generation + 1), generation) != generation)
			return;

		var  world        = _world;
		var  chunkMatches = _chunkMatches;
		var  entities     = _entities;
		bool usesShared   = _usesSharedScratch;

		_world             = null;
		_chunkMatches      = null;
		_entities          = null;
		_usesSharedScratch = false;

		if (world is null || chunkMatches is null || entities is null)
			return;

		world.ReleaseCursorQueryExecution(chunkMatches, entities, usesShared, this);
	}

	internal readonly struct IterationScope(QueryExecutionLease lease) : IDisposable
	{
		public void Dispose() => Interlocked.Decrement(ref lease._activeIterations);
	}
}

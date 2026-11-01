using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Internal.Fixed;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Internal;

internal sealed class WorldQueryEngine(World world, WorldConfig config)
{
	private readonly ConcurrentDictionary<Type, CompiledQueryPlan> _compiledPlansBySpecType = [];
	private readonly object _compiledPlansGate = new();
	private readonly ConcurrentBag<QueryExecutionLease> _queryExecutionLeasePool = [];
	private readonly Entity[] _queryEntities = new Entity[config.QueryResultCapacity];
	private readonly QueryChunkMatch[] _queryChunkMatches = new QueryChunkMatch[config.QueryResultCapacity];
	private readonly WorldConfig _config = config;
	private readonly World _world = world;
	private int _activeQueryIterations;
	private int _sharedQueryScratchInUse;

	public bool HasActiveQueryIterations => Volatile.Read(ref _activeQueryIterations) > 0;

	public QueryChunkMatch[] SharedChunkMatches => _queryChunkMatches;

	public QueryHandle<TSpec> Compile<TSpec>() where TSpec : struct, ICompiledQuerySpec
	{
		var specType = typeof(TSpec);
		if (_compiledPlansBySpecType.TryGetValue(specType, out var existing))
			return new(existing);

		lock (_compiledPlansGate)
		{
			if (_compiledPlansBySpecType.TryGetValue(specType, out existing))
				return new(existing);

			var builder = new QueryBuilder(_world);
			var spec = default(TSpec);
			spec.Build(ref builder);
			var plan = builder.Build();
			_world.EnableRefWriteTrackingForQuery(plan);

			_compiledPlansBySpecType[specType] = plan;
			return new(plan);
		}
	}

	public QueryCursor Execute<TSpec>(QueryHandle<TSpec> handle) where TSpec : struct, ICompiledQuerySpec
	{
		_world.ValidateDirectIterationHandle(handle);

		AcquireQueryExecutionScratch(out var chunkMatches, out var entities, out bool usesSharedScratch);
		try
		{
			int matchCount = _world.FillQueryResultsForQueryEngine(
				handle.Plan,
				chunkMatches,
				_config.QueryResultCapacity,
				out int chunkMatchCount
			);
			var lease = RentQueryExecutionLease(chunkMatches, entities, usesSharedScratch);
			EnterQueryIteration();
			return new(_world, chunkMatches, chunkMatchCount, entities, matchCount, lease);
		}
		catch
		{
			ReleaseQueryExecutionScratch(chunkMatches, entities, usesSharedScratch);
			throw;
		}
	}

	public QueryDiagnostics GetDiagnostics(CompiledQueryPlan plan)
	{
		int matchingArchetypeCount = _world.GetOrRefreshMatchingArchetypesForQueryEngine(plan);
		AcquireQueryChunkMatchScratch(out var chunkMatches, out bool usesSharedScratch);
		try
		{
			int matchingEntityCount = _world.FillQueryResultsForQueryEngine(
				plan,
				chunkMatches,
				_config.QueryResultCapacity,
				out int matchingChunkCount,
				false
			);
			return new(
				matchingArchetypeCount,
				matchingChunkCount,
				matchingEntityCount,
				plan.ArchetypeCacheVersion,
				plan.ArchetypeCacheVersion == _world.ArchetypeVersionForQueryEngine,
				_world.ResolveTypesForQueryEngine(plan.AllTypeIds),
				_world.ResolveTypesForQueryEngine(plan.AnyTypeIds),
				_world.ResolveTypesForQueryEngine(plan.NoneTypeIds),
				_world.ResolveTypesForQueryEngine(plan.OptionalTypeIds),
				_world.ResolveTypesForQueryEngine(plan.AddedTypeIds),
				_world.ResolveTypesForQueryEngine(plan.ChangedTypeIds),
				plan.RelatedRelationType,
				plan.RelatedTarget
			);
		}
		finally
		{
			ReleaseQueryChunkMatchScratch(chunkMatches, usesSharedScratch);
		}
	}

	public void ClearCompiledPlans()
	{
		lock (_compiledPlansGate)
			_compiledPlansBySpecType.Clear();
	}

	public void MaterializeQueryEntities(
		QueryChunkMatch[] chunkMatches,
		int               chunkMatchCount,
		Entity[]          destination,
		int               entityCount)
	{
		if (entityCount == 0)
			return;

		var writeIndex = 0;
		for (var i = 0; i < chunkMatchCount; i++)
		{
			var match = chunkMatches[i];
			var chunk = _world.GetArchetypeForCursor(match.ArchetypeId).GetChunk(match.ChunkIndex);
			int rowEnd = match.RowStart + match.Count;
			for (int row = match.RowStart; row < rowEnd; row++)
			{
				int entityId = chunk.EntityIds[row];
				destination[writeIndex++] = _world.GetEntityForCursor(entityId);
			}
		}

		if (writeIndex != entityCount)
			throw new InvalidOperationException("Query entity materialization count mismatch.");
	}

	public void ReleaseCursorQueryExecution(
		QueryChunkMatch[]   chunkMatches,
		Entity[]            entities,
		bool                usesSharedScratch,
		QueryExecutionLease lease)
	{
		ReleaseQueryExecutionScratch(chunkMatches, entities, usesSharedScratch);
		ExitQueryIteration();
		_queryExecutionLeasePool.Add(lease);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal void EnterQueryIteration() => Interlocked.Increment(ref _activeQueryIterations);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal void ExitQueryIteration() => Interlocked.Decrement(ref _activeQueryIterations);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private QueryExecutionLease RentQueryExecutionLease(
		QueryChunkMatch[] chunkMatches,
		Entity[]          entities,
		bool              usesSharedScratch)
	{
		if (!_queryExecutionLeasePool.TryTake(out var lease))
			lease = new();

		lease.Initialize(_world, chunkMatches, entities, usesSharedScratch);
		return lease;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void AcquireQueryChunkMatchScratch(out QueryChunkMatch[] chunkMatches, out bool usesSharedScratch)
	{
		if (TryAcquireSharedQueryScratch())
		{
			chunkMatches = _queryChunkMatches;
			usesSharedScratch = true;
			return;
		}

		chunkMatches = ArrayPool<QueryChunkMatch>.Shared.Rent(_config.QueryResultCapacity);
		usesSharedScratch = false;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void AcquireQueryExecutionScratch(
		out QueryChunkMatch[] chunkMatches,
		out Entity[]          entities,
		out bool              usesSharedScratch)
	{
		if (TryAcquireSharedQueryScratch())
		{
			chunkMatches = _queryChunkMatches;
			entities = _queryEntities;
			usesSharedScratch = true;
			return;
		}

		chunkMatches = ArrayPool<QueryChunkMatch>.Shared.Rent(_config.QueryResultCapacity);
		entities = ArrayPool<Entity>.Shared.Rent(_config.QueryResultCapacity);
		usesSharedScratch = false;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void ReleaseQueryChunkMatchScratch(QueryChunkMatch[] chunkMatches, bool usesSharedScratch)
	{
		if (usesSharedScratch)
		{
			Volatile.Write(ref _sharedQueryScratchInUse, 0);
			return;
		}

		ArrayPool<QueryChunkMatch>.Shared.Return(chunkMatches);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void ReleaseQueryExecutionScratch(
		QueryChunkMatch[] chunkMatches,
		Entity[]          entities,
		bool              usesSharedScratch)
	{
		if (usesSharedScratch)
		{
			Volatile.Write(ref _sharedQueryScratchInUse, 0);
			return;
		}

		ArrayPool<QueryChunkMatch>.Shared.Return(chunkMatches);
		ArrayPool<Entity>.Shared.Return(entities);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryAcquireSharedQueryScratch() =>
		Interlocked.CompareExchange(ref _sharedQueryScratchInUse, 1, 0) == 0;
}

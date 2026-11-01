using Bezoro.ECS.Internal.Fixed;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Internal;

internal sealed class WorldQueryRuntime(
	WorldEntityStore   entityStore,
	WorldRelationIndex relationIndex,
	WorldChangeTracker changeTracker)
{
	private readonly WorldChangeTracker _changeTracker = changeTracker;
	private readonly WorldEntityStore _entityStore = entityStore;
	private readonly WorldRelationIndex _relationIndex = relationIndex;

	public int FillQueryResults(
		CompiledQueryPlan plan,
		QueryChunkMatch[] chunkMatches,
		int               entityCapacity,
		out int           chunkMatchCount,
		bool              advanceObservedChangeVersion = true)
	{
		lock (plan)
		{
			var state = new QueryFillState(chunkMatches, entityCapacity);
			ulong queryVersion = _changeTracker.CurrentChangeVersion;
			int matchCount = GetOrRefreshMatchingArchetypes(plan);
			int[] matches = plan.MatchingArchetypeIds;
			bool requiresChangedFilter = plan.ChangedTypeIds.Length > 0 || plan.AddedTypeIds.Length > 0;
			for (var i = 0; i < matchCount; i++)
			{
				var archetype = _entityStore.Archetypes[matches[i]];
				bool appended = requiresChangedFilter
					? TryAppendChangedChunkMatches(plan, matches[i], archetype, ref state)
					: TryAppendAllChunkMatches(matches[i], archetype, ref state);
				if (!appended)
					goto done;
			}

			done:
			if (advanceObservedChangeVersion)
				plan.LastObservedChangeVersion = queryVersion;

			chunkMatchCount = state.ChunkMatchCount;
			_changeTracker.ObserveQueryEntityCount(state.Count);
			return state.Count;
		}
	}

	public int GetOrRefreshMatchingArchetypes(CompiledQueryPlan plan)
	{
		lock (plan)
		{
			if (plan.ArchetypeCacheVersion == _entityStore.ArchetypeVersion)
				return plan.MatchingArchetypeCount;

			int[] matches = plan.MatchingArchetypeIds;
			if (matches.Length < _entityStore.Archetypes.Count)
				plan.MatchingArchetypeIds = matches = new int[_entityStore.Archetypes.Count];

			var count = 0;
			for (var i = 0; i < _entityStore.Archetypes.Count; i++)
			{
				var archetype = _entityStore.Archetypes[i];
				if (!ArchetypeMatches(plan, archetype.MaskWords))
					continue;

				if (!ArchetypeMatchesRelationFilter(plan, archetype))
					continue;

				matches[count++] = i;
			}

			plan.MatchingArchetypeCount = count;
			plan.ArchetypeCacheVersion = _entityStore.ArchetypeVersion;
			return count;
		}
	}

	public Type[] ResolveTypes(int[] typeIds)
	{
		if (typeIds.Length == 0)
			return [];

		var resolved = new Type[typeIds.Length];
		for (var i = 0; i < typeIds.Length; i++)
		{
			int typeId = typeIds[i];
			resolved[i] = _entityStore.TypeById[typeId] ??
				throw new InvalidOperationException(
					$"Component type id '{typeId}' is not registered."
				);
		}

		return resolved;
	}

	private bool ArchetypeMatches(CompiledQueryPlan plan, ulong[] archetypeMaskWords)
	{
		for (var i = 0; i < plan.AllMaskWordIndices.Length; i++)
		{
			int wordIndex = plan.AllMaskWordIndices[i];
			ulong allWord = plan.AllMaskWords[wordIndex];
			if ((archetypeMaskWords[wordIndex] & allWord) != allWord)
				return false;
		}

		for (var i = 0; i < plan.NoneMaskWordIndices.Length; i++)
		{
			int wordIndex = plan.NoneMaskWordIndices[i];
			if ((archetypeMaskWords[wordIndex] & plan.NoneMaskWords[wordIndex]) != 0UL)
				return false;
		}

		if (plan.AnyTypeIds.Length == 0)
			return true;

		for (var i = 0; i < plan.AnyMaskWordIndices.Length; i++)
		{
			int wordIndex = plan.AnyMaskWordIndices[i];
			if ((archetypeMaskWords[wordIndex] & plan.AnyMaskWords[wordIndex]) != 0UL)
				return true;
		}

		return false;
	}

	private bool ArchetypeMatchesRelationFilter(CompiledQueryPlan plan, ArchetypeStorage archetype)
	{
		Type? relationType = plan.RelatedRelationType;
		if (relationType is null)
			return true;

		Entity target = plan.RelatedTarget;
		if (target == Entity.Wildcard)
		{
			int[] relationTypeIds = _relationIndex.GetRelationTypeIds(relationType);
			for (var i = 0; i < relationTypeIds.Length; i++)
			{
				if (archetype.HasType(relationTypeIds[i]))
					return true;
			}

			return false;
		}

		return _relationIndex.TryGetRelationTypeId(relationType, target, out int relationTypeId) &&
			   archetype.HasType(relationTypeId);
	}

	private bool TryAppendQueryChunk(
		in QueryChunkMatch chunkMatch,
		ref QueryFillState state)
	{
		if (state.ChunkMatchCount >= state.ChunkMatches.Length)
		{
			_changeTracker.HandleQueryOverflow();
			return false;
		}

		state.ChunkMatches[state.ChunkMatchCount++] = chunkMatch;
		return true;
	}

	private bool TryAppendQueryRange(
		int               archetypeId,
		int               chunkIndex,
		int               rowStart,
		int               rowCount,
		ref QueryFillState state)
	{
		int remaining = state.EntityCapacity - state.Count;
		if (remaining <= 0)
		{
			_changeTracker.HandleQueryOverflow();
			return false;
		}

		int rowsToAppend = Math.Min(remaining, rowCount);
		if (!TryAppendQueryChunk(
				new(archetypeId, chunkIndex, rowStart, rowsToAppend, state.Count),
				ref state
			))
			return false;

		state.Count += rowsToAppend;
		if (rowsToAppend >= rowCount)
			return true;

		_changeTracker.HandleQueryOverflow();
		return false;
	}

	private bool TryAppendAllChunkMatches(int archetypeId, ArchetypeStorage archetype, ref QueryFillState state)
	{
		for (var chunkIndex = 0; chunkIndex < archetype.ChunkCount; chunkIndex++)
		{
			var chunk = archetype.GetChunk(chunkIndex);
			if (chunk.Count == 0)
				continue;

			if (!TryAppendQueryRange(archetypeId, chunkIndex, 0, chunk.Count, ref state))
				return false;
		}

		return true;
	}

	private bool TryAppendChangedChunkMatches(
		CompiledQueryPlan plan,
		int               archetypeId,
		ArchetypeStorage  archetype,
		ref QueryFillState state)
	{
		for (var chunkIndex = 0; chunkIndex < archetype.ChunkCount; chunkIndex++)
		{
			var chunk = archetype.GetChunk(chunkIndex);
			if (chunk.Count == 0)
				continue;

			var row = 0;
			while (row < chunk.Count)
			{
				while (row < chunk.Count && !_changeTracker.EntityMatchesChangedFilters(plan, chunk.EntityIds[row]))
					row++;

				if (row >= chunk.Count)
					break;

				int runStart = row;
				row++;
				while (row < chunk.Count && _changeTracker.EntityMatchesChangedFilters(plan, chunk.EntityIds[row]))
					row++;

				if (!TryAppendQueryRange(archetypeId, chunkIndex, runStart, row - runStart, ref state))
					return false;
			}
		}

		return true;
	}

	private ref struct QueryFillState(QueryChunkMatch[] chunkMatches, int entityCapacity)
	{
		public readonly QueryChunkMatch[] ChunkMatches = chunkMatches;
		public readonly int EntityCapacity = entityCapacity;
		public int ChunkMatchCount;
		public int Count;
	}
}

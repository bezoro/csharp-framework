using Bezoro.ECS.Internal.Fixed;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Internal;

internal sealed class WorldChangeTracker(WorldEntityStore entityStore, WorldConfig config)
{
	private readonly Dictionary<long, ulong> _addedVersionByEntityType = [];
	private readonly Dictionary<long, ulong> _changeVersionByEntityType = [];
	private readonly WorldConfig _config = config;
	private readonly WorldEntityStore _entityStore = entityStore;
	private readonly object _sync = new();
	private ulong _componentChangeVersion;
	private int _queryHighWatermark;
	private int _queryOverflowCount;
	private bool _trackRefWriteChanges;

	public ulong CurrentChangeVersion { get { lock (_sync) return _componentChangeVersion; } }

	public int QueryHighWatermark => _queryHighWatermark;

	public int QueryOverflowCount => _queryOverflowCount;

	public ulong AdvanceComponentChangeVersion()
	{
		lock (_sync) return ++_componentChangeVersion;
	}

	public void Clear()
	{
		lock (_sync)
		{
			_addedVersionByEntityType.Clear();
			_changeVersionByEntityType.Clear();
		}
	}

	public void ClearEntityComponentVersions(int entityId, int typeCount)
	{
		lock (_sync)
		{
			for (var typeId = 0; typeId < typeCount; typeId++)
			{
				long key = ComposeEntityTypeKey(entityId, typeId);
				_addedVersionByEntityType.Remove(key);
				_changeVersionByEntityType.Remove(key);
			}
		}
	}

	public bool EntityMatchesChangedFilters(CompiledQueryPlan plan, int entityId)
	{
		lock (_sync)
		{
			ulong lastObservedVersion = plan.LastObservedChangeVersion;
			for (var i = 0; i < plan.AddedTypeIds.Length; i++)
			{
				long key = ComposeEntityTypeKey(entityId, plan.AddedTypeIds[i]);
				if (!_addedVersionByEntityType.TryGetValue(key, out ulong addedVersion))
					return false;

				if (addedVersion <= lastObservedVersion)
					return false;
			}

			for (var i = 0; i < plan.ChangedTypeIds.Length; i++)
			{
				long key = ComposeEntityTypeKey(entityId, plan.ChangedTypeIds[i]);
				if (!_changeVersionByEntityType.TryGetValue(key, out ulong changeVersion))
					return false;

				if (changeVersion <= lastObservedVersion)
					return false;
			}

			return true;
		}
	}

	public void EnableRefWriteTracking(CompiledQueryPlan plan)
	{
		if (plan.ChangedTypeIds.Length > 0 || plan.AddedTypeIds.Length > 0)
			_trackRefWriteChanges = true;
	}

	public void HandleQueryOverflow()
	{
		Interlocked.Increment(ref _queryOverflowCount);
		if (_config.OverflowPolicy == WorldOverflowPolicy.FailFast)
			throw new InvalidOperationException(
				$"Query result capacity '{_config.QueryResultCapacity}' was exceeded."
			);
	}

	public void MarkComponentAdded(int entityId, int typeId, ulong version)
	{
		lock (_sync) _addedVersionByEntityType[ComposeEntityTypeKey(entityId, typeId)] = version;
	}

	public void MarkComponentChanged(int entityId, int typeId, ulong version)
	{
		lock (_sync) _changeVersionByEntityType[ComposeEntityTypeKey(entityId, typeId)] = version;
	}

	public void ObserveDirectIterationEntityCount(int processedEntityCount)
		=> ObserveQueryEntityCount(processedEntityCount);

	public void ObserveQueryEntityCount(int count)
	{
		int observed = Volatile.Read(ref _queryHighWatermark);
		while (count > observed)
		{
			int previous = Interlocked.CompareExchange(ref _queryHighWatermark, count, observed);
			if (previous == observed) return;
			observed = previous;
		}
	}

	public void TrackPotentialAccessorRefWrite(
		ArchetypeStorage.Chunk chunk,
		int                    columnIndex,
		int                    typeId,
		int                    rowIndex)
	{
		if (!_trackRefWriteChanges)
			return;

		if (unchecked((uint)rowIndex) >= unchecked((uint)chunk.Count))
			return;

		ulong changeVersion = AdvanceComponentChangeVersion();
		chunk.ComponentChangeVersions[columnIndex] = changeVersion;
		MarkComponentChanged(chunk.EntityIds[rowIndex], typeId, changeVersion);
	}

	public void TrackPotentialChunkMatchRefWrites(
		QueryChunkMatch[] chunkMatches,
		int               chunkMatchCount,
		int               typeId)
	{
		if (!_trackRefWriteChanges)
			return;

		for (var i = 0; i < chunkMatchCount; i++)
		{
			var match = chunkMatches[i];
			if (match.Count == 0)
				continue;

			var archetype = _entityStore.GetArchetypeForCursor(match.ArchetypeId);
			int columnIndex = archetype.GetColumnIndexOrNegative(typeId);
			if (columnIndex < 0)
				continue;

			var chunk = archetype.GetChunkUnchecked(match.ChunkIndex);
			ulong changeVersion = AdvanceComponentChangeVersion();
			chunk.ComponentChangeVersions[columnIndex] = changeVersion;
			int rowEnd = match.RowStart + match.Count;
			for (var row = match.RowStart; row < rowEnd; row++)
				MarkComponentChanged(chunk.EntityIds[row], typeId, changeVersion);
		}
	}

	public void TrackPotentialCursorRefWrite(
		ArchetypeStorage.Chunk chunk,
		int                    columnIndex,
		int                    typeId,
		int                    rowIndex) =>
		TrackPotentialAccessorRefWrite(chunk, columnIndex, typeId, rowIndex);

	public void TrackPotentialDirectFastRefWrites(int[] matchingArchetypeIds, int matchCount, int typeId)
	{
		if (!_trackRefWriteChanges)
			return;

		for (var i = 0; i < matchCount; i++)
		{
			var archetype = _entityStore.GetArchetypeForCursor(matchingArchetypeIds[i]);
			int columnIndex = archetype.GetColumnIndexOrNegative(typeId);
			if (columnIndex < 0)
				continue;

			for (var chunkIndex = 0; chunkIndex < archetype.ChunkCount; chunkIndex++)
			{
				var chunk = archetype.GetChunkUnchecked(chunkIndex);
				if (chunk.Count == 0)
					continue;

				ulong changeVersion = AdvanceComponentChangeVersion();
				chunk.ComponentChangeVersions[columnIndex] = changeVersion;
				for (var row = 0; row < chunk.Count; row++)
					MarkComponentChanged(chunk.EntityIds[row], typeId, changeVersion);
			}
		}
	}

	private static long ComposeEntityTypeKey(int entityId, int typeId) => (long)entityId << 32 | unchecked((uint)typeId);
}

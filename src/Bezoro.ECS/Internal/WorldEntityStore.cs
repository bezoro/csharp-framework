using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Bezoro.ECS.Internal.Fixed;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Internal;

internal sealed class WorldEntityStore
{
	private readonly World              _world;
	private readonly WorldConfig        _config;
	private readonly WorldRelationIndex _relationIndex;
	private readonly object _typeRegistrationGate = new();

	public WorldEntityStore(World world, WorldConfig config, WorldRelationIndex relationIndex)
	{
		_world         = world ?? throw new ArgumentNullException(nameof(world));
		_config        = config ?? throw new ArgumentNullException(nameof(config));
		_relationIndex = relationIndex ?? throw new ArgumentNullException(nameof(relationIndex));

		AliveByEntityId    = new bool[_config.EntityCapacity];
		VersionByEntityId  = new int[_config.EntityCapacity];
		FreeEntityIds      = new int[_config.EntityCapacity];
		LocationByEntityId = new Fixed.EntityLocation[_config.EntityCapacity];
		for (var i = 0; i < LocationByEntityId.Length; i++)
			LocationByEntityId[i] = Fixed.EntityLocation.Invalid;

		TypeById          = new Type[_config.ComponentTypeCapacity];
		TypeIsManagedLane = new bool[_config.ComponentTypeCapacity];
		MaskWordCount     = GetMaskWordCount(_config.ComponentTypeCapacity);
		EmptyArchetypeId  = GetOrCreateArchetype([]);
	}

	public bool[] AliveByEntityId { get; }

	public bool[] TypeIsManagedLane { get; }

	public Dictionary<ArchetypeTypeSetKey, int> ArchetypeByTypeSet { get; } = new(ArchetypeTypeSetKeyComparer.Instance);

	public Dictionary<long, int[]> TransitionCopyMapByPair { get; } = [];

	public ConcurrentDictionary<Type, int> TypeToId { get; } = new();

	public Fixed.EntityLocation[] LocationByEntityId { get; }

	public int EmptyArchetypeId { get; }

	public int MaskWordCount { get; }

	public int[] FreeEntityIds { get; }

	public int[] VersionByEntityId { get; }

	public List<ArchetypeStorage> Archetypes { get; } = [];

	public Type?[] TypeById { get; }

	public int AliveCount { get; set; }

	public int ArchetypeVersion { get; set; }

	public int ComponentTypeOverflowCount { get; set; }

	public int EntityHighWatermark { get; set; }

	public int EntityOverflowCount { get; set; }

	public int FreeCount { get; set; }

	public int NextEntityId { get; set; }

	public int RegisteredTypeHighWatermark { get; set; }

	public int TypeCount { get; set; }

	public ArchetypeStorage GetArchetypeForCursor(int archetypeId)
	{
		if (unchecked((uint)archetypeId) >= unchecked((uint)Archetypes.Count))
			throw new ArgumentOutOfRangeException(nameof(archetypeId));

		return Archetypes[archetypeId];
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool HasComponentForAccessor(
		Entity  entity,
		int     typeId,
		ref int cachedArchetypeId,
		ref int cachedColumnIndex)
	{
		if (!IsAlive(entity))
			return false;

		var location = LocationByEntityId[entity.Id];
		if (!location.IsValid)
			return false;

		var archetype = Archetypes[location.ArchetypeId];
		return TryResolveAccessorColumnIndex(
			archetype,
			location.ArchetypeId,
			typeId,
			ref cachedArchetypeId,
			ref cachedColumnIndex
		);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool IsAlive(Entity entity) =>
		entity.Id >= 0 &&
		entity.Id < AliveByEntityId.Length &&
		AliveByEntityId[entity.Id] &&
		VersionByEntityId[entity.Id] == entity.Version;

	public bool MatchesRemoveTransitionSource(
		Entity entity,
		int    sourceArchetypeId,
		int    typeId,
		int    targetArchetypeId)
	{
		EnsureAlive(entity);
		var location = LocationByEntityId[entity.Id];
		if (!location.IsValid)
			throw new InvalidOperationException($"Entity '{entity.Id}' is not in a valid archetype.");

		if (location.ArchetypeId != sourceArchetypeId)
			return false;

		var sourceArchetype = Archetypes[sourceArchetypeId];
		if (targetArchetypeId == sourceArchetypeId)
			return !sourceArchetype.HasType(typeId);

		return sourceArchetype.HasType(typeId);
	}

	public bool MatchesSetTransitionSource(
		Entity entity,
		int    sourceArchetypeId,
		int    typeId,
		int    targetArchetypeId)
	{
		EnsureAlive(entity);
		var location = LocationByEntityId[entity.Id];
		if (!location.IsValid)
			throw new InvalidOperationException($"Entity '{entity.Id}' is not in a valid archetype.");

		if (location.ArchetypeId != sourceArchetypeId)
			return false;

		var sourceArchetype = Archetypes[sourceArchetypeId];
		if (targetArchetypeId == sourceArchetypeId)
			return sourceArchetype.HasType(typeId);

		return !sourceArchetype.HasType(typeId);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool TryGetComponentForAccessor<T>(
		Entity  entity,
		int     typeId,
		ref int cachedArchetypeId,
		ref int cachedColumnIndex,
		out T   component)
		where T : unmanaged
	{
		if (!IsAlive(entity))
		{
			component = default;
			return false;
		}

		var location = LocationByEntityId[entity.Id];
		if (!location.IsValid)
		{
			component = default;
			return false;
		}

		var archetype = Archetypes[location.ArchetypeId];
		if (!TryResolveAccessorColumnIndex(
				archetype,
				location.ArchetypeId,
				typeId,
				ref cachedArchetypeId,
				ref cachedColumnIndex
			))
		{
			component = default;
			return false;
		}

		var chunk = archetype.GetChunkUnchecked(location.ChunkIndex);
		component = archetype.GetRefByIndex<T>(chunk, cachedColumnIndex, location.RowIndex);
		return true;
	}

	public bool TryGetComponentUnchecked<T>(int entityId, int typeId, out T component) where T : struct
	{
		var location = LocationByEntityId[entityId];
		if (!location.IsValid)
		{
			component = default;
			return false;
		}

		var archetype   = Archetypes[location.ArchetypeId];
		int columnIndex = archetype.GetColumnIndexOrNegative(typeId);
		if (columnIndex < 0)
		{
			component = default;
			return false;
		}

		var chunk = archetype.GetChunkUnchecked(location.ChunkIndex);
		if (unchecked((uint)location.RowIndex) >= unchecked((uint)chunk.Count))
		{
			component = default;
			return false;
		}

		component = archetype.GetRefByIndex<T>(chunk, columnIndex, location.RowIndex);
		return true;
	}

	public Entity CreateEntity()
	{
		bool fromFreeList = FreeCount > 0;
		int id;
		if (fromFreeList)
		{
			id = FreeEntityIds[FreeCount - 1];
		}
		else
		{
			if (NextEntityId >= _config.EntityCapacity)
			{
				EntityOverflowCount++;
				throw new InvalidOperationException(
					$"Entity capacity '{_config.EntityCapacity}' was exceeded."
				);
			}

			id = NextEntityId;
		}

		var emptyArchetype = Archetypes[EmptyArchetypeId];
		emptyArchetype.AllocateRow(id, out int chunkIndex, out int rowIndex);
		if (fromFreeList) FreeCount--;
		else NextEntityId++;
		AliveByEntityId[id] = true;
		AliveCount++;
		if (AliveCount > EntityHighWatermark)
			EntityHighWatermark = AliveCount;

		LocationByEntityId[id] = new(EmptyArchetypeId, chunkIndex, rowIndex);

		return new(id, VersionByEntityId[id]);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Entity GetEntityForCursor(int entityId) =>
		new(entityId, VersionByEntityId[entityId]);

	public int CreateRelationTypeId()
	{
		lock (_typeRegistrationGate)
		{
			if (TypeCount >= TypeById.Length)
			{
				ComponentTypeOverflowCount++;
				throw new InvalidOperationException(
					$"Component type capacity '{_config.ComponentTypeCapacity}' was exceeded."
				);
			}

			int typeId = TypeCount++;
			TypeById[typeId]          = typeof(RelationMarker);
			TypeIsManagedLane[typeId] = false;
			if (TypeCount > RegisteredTypeHighWatermark)
				RegisteredTypeHighWatermark = TypeCount;

			return typeId;
		}
	}

	public int GetArchetypeEntityCount(int archetypeId)
	{
		if (unchecked((uint)archetypeId) >= unchecked((uint)Archetypes.Count))
			throw new ArgumentOutOfRangeException(nameof(archetypeId));

		return Archetypes[archetypeId].EntityCount;
	}

	public int GetOrCreateComponentTypeId(Type componentType, bool containsReferences)
	{
		lock (_typeRegistrationGate)
		{
			if (componentType is null) throw new ArgumentNullException(nameof(componentType));
			if (!componentType.IsValueType)
				throw new ArgumentException("Components must be value types.", nameof(componentType));

			if (TypeToId.TryGetValue(componentType, out int existing))
				return existing;

			if (TypeCount >= TypeById.Length)
			{
				ComponentTypeOverflowCount++;
				throw new InvalidOperationException(
					$"Component type capacity '{_config.ComponentTypeCapacity}' was exceeded."
				);
			}

			int typeId = TypeCount++;
			TypeById[typeId]          = componentType;
			TypeIsManagedLane[typeId] = containsReferences;
			TypeToId[componentType]   = typeId;
			if (TypeCount > RegisteredTypeHighWatermark)
				RegisteredTypeHighWatermark = TypeCount;

			return typeId;
		}
	}

	public int GetOrCreateComponentTypeIdGeneric<T>() where T : struct
	{
		var componentType = typeof(T);
		if (TypeToId.TryGetValue(componentType, out int existing)) return existing;
		return GetOrCreateComponentTypeId(componentType, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
	}

	public int[] BuildMaskWordIndices(ulong[] maskWords)
	{
		var count = 0;
		for (var i = 0; i < maskWords.Length; i++)
		{
			if (maskWords[i] != 0UL)
				count++;
		}

		if (count == 0)
			return [];

		var indices = new int[count];
		var j       = 0;
		for (var i = 0; i < maskWords.Length; i++)
		{
			if (maskWords[i] == 0UL)
				continue;

			indices[j++] = i;
		}

		return indices;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int[] GetEntityVersionsForCursor() => VersionByEntityId;

	public ref T GetComponentRefForCursorUnchecked<T>(int entityId, int typeId) where T : struct
	{
		var location = LocationByEntityId[entityId];
		if (!location.IsValid)
			throw new InvalidOperationException($"Entity '{entityId}' is not in a valid archetype.");

		return ref Archetypes[location.ArchetypeId].GetRef<T>(location.ChunkIndex, location.RowIndex, typeId);
	}

	public ref T GetComponentRefUnchecked<T>(int entityId, int typeId) where T : struct
	{
		var location = LocationByEntityId[entityId];
		if (!location.IsValid)
			throw new InvalidOperationException($"Entity '{entityId}' is not in a valid archetype.");

		var archetype   = Archetypes[location.ArchetypeId];
		int columnIndex = archetype.GetColumnIndexOrNegative(typeId);
		if (columnIndex < 0)
			throw new KeyNotFoundException(
				$"Component '{typeof(T).Name}' was not found for entity '{entityId}:{VersionByEntityId[entityId]}'."
			);

		var chunk = archetype.GetChunkUnchecked(location.ChunkIndex);
		return ref archetype.GetRefByIndex<T>(chunk, columnIndex, location.RowIndex);
	}

	public Type[] ResolveTypes(int[] typeIds)
	{
		if (typeIds.Length == 0)
			return [];

		var resolved = new Type[typeIds.Length];
		for (var i = 0; i < typeIds.Length; i++)
		{
			int typeId = typeIds[i];
			resolved[i] = TypeById[typeId] ??
						  throw new InvalidOperationException(
							  $"Component type id '{typeId}' is not registered."
						  );
		}

		return resolved;
	}

	public ulong[] BuildMaskWords(int[] typeIds)
	{
		var words = new ulong[MaskWordCount];
		for (var i = 0; i < typeIds.Length; i++)
		{
			int typeId    = typeIds[i];
			int wordIndex = typeId >> 6;
			int bitIndex  = typeId & 63;
			words[wordIndex] |= 1UL << bitIndex;
		}

		return words;
	}

	public void ApplySetBatchFromCommandKnownTransition<T>(
		int[] entityIds,
		int   entityOffset,
		int   count,
		T[]   payloads,
		int   payloadCount,
		int[] payloadIndices,
		int   payloadOffset,
		int   typeId,
		int   sourceArchetypeId,
		int   targetArchetypeId)
		where T : struct
	{
		if (entityIds is null) throw new ArgumentNullException(nameof(entityIds));
		if (payloads is null) throw new ArgumentNullException(nameof(payloads));
		if (payloadIndices is null) throw new ArgumentNullException(nameof(payloadIndices));
		if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
		if (entityOffset < 0 || entityOffset + count > entityIds.Length)
			throw new ArgumentOutOfRangeException(nameof(entityOffset));

		if (payloadOffset < 0 || payloadOffset + count > payloadIndices.Length)
			throw new ArgumentOutOfRangeException(nameof(payloadOffset));

		var  sourceArchetype   = Archetypes[sourceArchetypeId];
		int  sourceColumnIndex = -1;
		ulong changeVersion     = 0;
		if (targetArchetypeId == sourceArchetypeId)
		{
			sourceColumnIndex = sourceArchetype.GetColumnIndexOrNegative(typeId);
			if (sourceColumnIndex < 0)
				throw new InvalidOperationException(
					$"Type id '{typeId}' does not exist in archetype '{sourceArchetypeId}'."
				);

			changeVersion = AdvanceComponentChangeVersion();
		}

		for (var i = 0; i < count; i++)
		{
			int payloadIndex = payloadIndices[payloadOffset + i];
			if (unchecked((uint)payloadIndex) >= unchecked((uint)payloadCount))
				throw new InvalidOperationException(
					$"Payload index '{payloadIndex}' is out of range for '{typeof(T).Name}'."
				);

			int              entityId  = entityIds[entityOffset + i];
			ref readonly var component = ref payloads[payloadIndex];
			var              location  = LocationByEntityId[entityId];
			if (!location.IsValid || location.ArchetypeId != sourceArchetypeId)
			{
				SetComponentInternal(entityId, typeId, in component);
				continue;
			}

			if (targetArchetypeId == sourceArchetypeId)
			{
				ref var existing = ref sourceArchetype.GetRef<T>(location.ChunkIndex, location.RowIndex, typeId);
				existing = component;
				var sourceChunk = sourceArchetype.GetChunkUnchecked(location.ChunkIndex);
				sourceArchetype.MarkComponentChanged(sourceChunk, sourceColumnIndex, changeVersion);
				MarkComponentChanged(entityId, typeId, changeVersion);
				continue;
			}

			MoveEntityToArchetypeWithSet(
				entityId,
				location,
				sourceArchetype,
				targetArchetypeId,
				typeId,
				in component
			);
		}
	}

	public void ApplySetBatchFromCommandKnownTransitionFast<T>(
		int[] entityIds,
		int   entityOffset,
		int   count,
		T[]   payloads,
		int[] payloadIndices,
		int   payloadOffset,
		int   typeId,
		int   sourceArchetypeId,
		int   targetArchetypeId)
		where T : struct
	{
		if (count == 0)
			return;

		var sourceArchetype = Archetypes[sourceArchetypeId];
		if (targetArchetypeId == sourceArchetypeId)
		{
			ulong changeVersion     = AdvanceComponentChangeVersion();
			int  sourceColumnIndex = sourceArchetype.GetColumnIndexOrNegative(typeId);
			if (sourceColumnIndex < 0)
				throw new InvalidOperationException(
					$"Type id '{typeId}' does not exist in archetype '{sourceArchetypeId}'."
				);

			int     cachedChunkIndex = -1;
			Span<T> cachedColumn     = default;
			for (var i = 0; i < count; i++)
			{
				int payloadIndex = payloadIndices[payloadOffset + i];
				int entityId     = entityIds[entityOffset + i];
				var location     = LocationByEntityId[entityId];
				if (location.ChunkIndex != cachedChunkIndex)
				{
					cachedChunkIndex = location.ChunkIndex;
					var sourceChunk = sourceArchetype.GetChunkUnchecked(cachedChunkIndex);
					cachedColumn = sourceArchetype.GetSpanByIndex<T>(sourceChunk, sourceColumnIndex);
					sourceArchetype.MarkComponentChanged(sourceChunk, sourceColumnIndex, changeVersion);
				}

				cachedColumn[location.RowIndex] = payloads[payloadIndex];
				MarkComponentChanged(entityId, typeId, changeVersion);
			}

			return;
		}

		var   targetArchetype         = Archetypes[targetArchetypeId];
		int[] sourceTargetColumnPairs = GetOrCreateTransitionCopyMap(sourceArchetype, targetArchetype);
		for (var i = 0; i < count; i++)
		{
			int payloadIndex = payloadIndices[payloadOffset + i];
			int entityId     = entityIds[entityOffset + i];
			var location     = LocationByEntityId[entityId];
			MoveEntityToArchetypeWithSet(
				entityId,
				location,
				sourceArchetype,
				targetArchetype,
				sourceTargetColumnPairs,
				typeId,
				in payloads[payloadIndex]
			);
		}
	}

	public void ApplySetFromCommand<T>(Entity entity, in T component) where T : struct
	{
		EnsureAlive(entity);
		if (!TypeToId.TryGetValue(typeof(T), out int typeId))
		{
			_world.EnsureStructuralChangesAllowed();
			typeId = GetOrCreateComponentTypeIdGeneric<T>();
		}
		SetComponentInternal(entity.Id, typeId, in component);
	}

	public void ApplySetFromCommandKnownTransition<T>(
		Entity entity,
		in T   component,
		int    typeId,
		int    sourceArchetypeId,
		int    targetArchetypeId)
		where T : struct
	{
		EnsureAlive(entity);
		var location = LocationByEntityId[entity.Id];
		if (!location.IsValid || location.ArchetypeId != sourceArchetypeId)
		{
			SetComponentInternal(entity.Id, typeId, in component);
			return;
		}

		var sourceArchetype = Archetypes[sourceArchetypeId];
		if (targetArchetypeId == sourceArchetypeId)
		{
			OverwriteComponent(entity.Id, location, sourceArchetype, typeId, in component);
			return;
		}

		MoveEntityToArchetypeWithSet(
			entity.Id,
			location,
			sourceArchetype,
			targetArchetypeId,
			typeId,
			in component
		);
	}

	public void DescribeRemoveTransition(
		Entity  entity,
		int     typeId,
		out int sourceArchetypeId,
		out int targetArchetypeId)
	{
		EnsureAlive(entity);
		var location = LocationByEntityId[entity.Id];
		if (!location.IsValid)
			throw new InvalidOperationException($"Entity '{entity.Id}' is not in a valid archetype.");

		sourceArchetypeId = location.ArchetypeId;
		var sourceArchetype = Archetypes[sourceArchetypeId];
		targetArchetypeId = sourceArchetype.HasType(typeId)
								? GetOrCreateRemoveTransition(sourceArchetype, typeId)
								: sourceArchetypeId;
	}

	public void DescribeSetTransition(Entity entity, int typeId, out int sourceArchetypeId, out int targetArchetypeId)
	{
		EnsureAlive(entity);
		var location = LocationByEntityId[entity.Id];
		if (!location.IsValid)
			throw new InvalidOperationException($"Entity '{entity.Id}' is not in a valid archetype.");

		sourceArchetypeId = location.ArchetypeId;
		var sourceArchetype = Archetypes[sourceArchetypeId];
		targetArchetypeId = sourceArchetype.HasType(typeId)
								? sourceArchetypeId
								: GetOrCreateAddTransition(sourceArchetype, typeId);
	}

	public void DestroyEntity(Entity entity)
	{
		EnsureAlive(entity);
		_relationIndex.ReleaseRelationsForTarget(entity, RemoveRelationTypeFromAllSources);
		int entityId  = entity.Id;
		var location  = LocationByEntityId[entityId];
		var archetype = Archetypes[location.ArchetypeId];
		if (archetype.RemoveAt(location.ChunkIndex, location.RowIndex, out int movedEntityId))
			UpdateMovedEntityLocation(location.ArchetypeId, location.ChunkIndex, location.RowIndex, movedEntityId);

		AliveByEntityId[entityId]    = false;
		LocationByEntityId[entityId] = Fixed.EntityLocation.Invalid;
		VersionByEntityId[entityId]++;
		FreeEntityIds[FreeCount++] = entityId;
		AliveCount--;
		_world.ClearEntityComponentVersionsForEntityStore(entityId, TypeCount);
	}

	public void Dispose()
	{
		for (var i = 0; i < Archetypes.Count; i++)
			Archetypes[i].Dispose();

		TypeToId.Clear();
		_relationIndex.Clear();
		ArchetypeByTypeSet.Clear();
		Archetypes.Clear();
		TransitionCopyMapByPair.Clear();
	}

	public void EnsureAlive(Entity entity)
	{
		if (!IsAlive(entity))
			throw new InvalidOperationException($"Entity '{entity.Id}:{entity.Version}' is not alive.");
	}

	public void RemoveAllComponentsFromCommandKnownTransitionFast(int sourceArchetypeId, int targetArchetypeId)
	{
		if (targetArchetypeId == sourceArchetypeId)
			return;

		var   sourceArchetype         = Archetypes[sourceArchetypeId];
		var   targetArchetype         = Archetypes[targetArchetypeId];
		int[] sourceTargetColumnPairs = GetOrCreateTransitionCopyMap(sourceArchetype, targetArchetype);
		for (var chunkIndex = 0; chunkIndex < sourceArchetype.ChunkCount; chunkIndex++)
		{
			var chunk = sourceArchetype.GetChunk(chunkIndex);
			if (chunk.Count == 0)
				continue;

			MoveEntireChunkToArchetype(
				sourceArchetype,
				chunkIndex,
				chunk,
				targetArchetype,
				sourceTargetColumnPairs
			);
		}
	}

	public void RemoveComponentBatchFromCommandKnownTransition(
		int[] entityIds,
		int   entityOffset,
		int   count,
		int   typeId,
		int   sourceArchetypeId,
		int   targetArchetypeId)
	{
		if (entityIds is null) throw new ArgumentNullException(nameof(entityIds));
		if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
		if (entityOffset < 0 || entityOffset + count > entityIds.Length)
			throw new ArgumentOutOfRangeException(nameof(entityOffset));

		var sourceArchetype = Archetypes[sourceArchetypeId];
		for (var i = 0; i < count; i++)
		{
			int entityId = entityIds[entityOffset + i];
			var location = LocationByEntityId[entityId];
			if (!location.IsValid || location.ArchetypeId != sourceArchetypeId)
			{
				RemoveComponentFromCommand(new(entityId, VersionByEntityId[entityId]), typeId);
				continue;
			}

			if (targetArchetypeId == sourceArchetypeId)
				continue;

			MoveEntityToArchetype(entityId, location, sourceArchetype, targetArchetypeId);
		}
	}

	public void RemoveComponentBatchFromCommandKnownTransitionFast(
		int[] entityIds,
		int   entityOffset,
		int   count,
		int   sourceArchetypeId,
		int   targetArchetypeId)
	{
		if (count == 0 || targetArchetypeId == sourceArchetypeId)
			return;

		var   sourceArchetype         = Archetypes[sourceArchetypeId];
		var   targetArchetype         = Archetypes[targetArchetypeId];
		int[] sourceTargetColumnPairs = GetOrCreateTransitionCopyMap(sourceArchetype, targetArchetype);
		for (var i = 0; i < count; i++)
		{
			int entityId = entityIds[entityOffset + i];
			var location = LocationByEntityId[entityId];
			MoveEntityToArchetype(
				entityId,
				location,
				sourceArchetype,
				targetArchetype,
				sourceTargetColumnPairs
			);
		}
	}

	public void RemoveComponentFromCommand(Entity entity, int typeId)
	{
		EnsureAlive(entity);
		if (unchecked((uint)typeId) >= unchecked((uint)_config.ComponentTypeCapacity))
			return;

		int entityId = entity.Id;
		var location = LocationByEntityId[entityId];
		if (!location.IsValid)
			return;

		var sourceArchetype = Archetypes[location.ArchetypeId];
		if (!sourceArchetype.HasType(typeId))
			return;

		_world.EnsureStructuralChangesAllowed();
		int targetArchetypeId = GetOrCreateRemoveTransition(sourceArchetype, typeId);
		MoveEntityToArchetype(entityId, location, sourceArchetype, targetArchetypeId);
	}

	public void RemoveComponentFromCommandKnownTransition(
		Entity entity,
		int    typeId,
		int    sourceArchetypeId,
		int    targetArchetypeId)
	{
		EnsureAlive(entity);
		var location = LocationByEntityId[entity.Id];
		if (!location.IsValid || location.ArchetypeId != sourceArchetypeId)
		{
			RemoveComponentFromCommand(entity, typeId);
			return;
		}

		if (targetArchetypeId == sourceArchetypeId)
			return;

		var sourceArchetype = Archetypes[sourceArchetypeId];
		MoveEntityToArchetype(entity.Id, location, sourceArchetype, targetArchetypeId);
	}

	public void RemoveMarkedComponentsFromCommandKnownTransitionFast(
		uint[] batchEntityMarkerBits,
		int    sourceArchetypeId,
		int    targetArchetypeId)
	{
		if (targetArchetypeId == sourceArchetypeId)
			return;

		var   sourceArchetype         = Archetypes[sourceArchetypeId];
		var   targetArchetype         = Archetypes[targetArchetypeId];
		int[] sourceTargetColumnPairs = GetOrCreateTransitionCopyMap(sourceArchetype, targetArchetype);
		for (var chunkIndex = 0; chunkIndex < sourceArchetype.ChunkCount; chunkIndex++)
		{
			var chunk = sourceArchetype.GetChunk(chunkIndex);
			if (chunk.Count == 0)
				continue;

			if (IsChunkFullyMarked(batchEntityMarkerBits, chunk))
			{
				MoveEntireChunkToArchetype(
					sourceArchetype,
					chunkIndex,
					chunk,
					targetArchetype,
					sourceTargetColumnPairs
				);

				continue;
			}

			int rowCount = chunk.Count;
			var writeRow = 0;
			var readRow  = 0;
			while (readRow < rowCount)
			{
				int entityId = chunk.EntityIds[readRow];
				if (unchecked((uint)entityId) < unchecked((uint)_config.EntityCapacity) &&
					IsEntityMarked(batchEntityMarkerBits, entityId))
				{
					targetArchetype.AllocateRow(entityId, out int targetChunkIndex, out int targetRowIndex);
					var targetChunk = targetArchetype.GetChunk(targetChunkIndex);
					targetArchetype.CopySharedColumnsFromWithPairs(
						chunk,
						readRow,
						targetChunk,
						targetRowIndex,
						sourceTargetColumnPairs
					);

					LocationByEntityId[entityId] = new(targetArchetype.Id, targetChunkIndex, targetRowIndex);
					readRow++;
					continue;
				}

				int runStart = readRow;
				readRow++;
				while (readRow < rowCount)
				{
					int runEntityId = chunk.EntityIds[readRow];
					if (unchecked((uint)runEntityId) < unchecked((uint)_config.EntityCapacity) &&
						IsEntityMarked(batchEntityMarkerBits, runEntityId))
						break;

					readRow++;
				}

				int runLength = readRow - runStart;
				if (writeRow != runStart)
				{
					sourceArchetype.MoveRowRangeWithinChunk(chunk, runStart, writeRow, runLength);

					int runWriteEnd = writeRow + runLength;
					for (int row = writeRow; row < runWriteEnd; row++)
					{
						int survivorEntityId = chunk.EntityIds[row];
						if (unchecked((uint)survivorEntityId) < unchecked((uint)_config.EntityCapacity))
							LocationByEntityId[survivorEntityId] = new(sourceArchetypeId, chunkIndex, row);
					}
				}

				writeRow += runLength;
			}

			sourceArchetype.FinalizeChunkCompaction(chunkIndex, writeRow);
		}
	}

	public void Reset()
	{
		for (var entityId = 0; entityId < NextEntityId; entityId++)
		{
			AliveByEntityId[entityId] = false;
			VersionByEntityId[entityId]++;
			LocationByEntityId[entityId] = Fixed.EntityLocation.Invalid;
		}

		for (var i = 0; i < Archetypes.Count; i++)
			Archetypes[i].Clear();

		AliveCount   = 0;
		FreeCount    = 0;
		NextEntityId = 0;
		_relationIndex.Clear();
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void ResolveAccessorLocation(
		Entity               entity,
		int                  typeId,
		Type                 componentType,
		ref int              cachedArchetypeId,
		ref int              cachedColumnIndex,
		out ArchetypeStorage archetype,
		out int              chunkIndex,
		out int              rowIndex)
	{
		if (componentType is null) throw new ArgumentNullException(nameof(componentType));
		if (!IsAlive(entity))
			throw new InvalidOperationException($"Entity '{entity.Id}:{entity.Version}' is not alive.");

		var location = LocationByEntityId[entity.Id];
		if (!location.IsValid)
			throw new InvalidOperationException($"Entity '{entity.Id}' is not in a valid archetype.");

		archetype = Archetypes[location.ArchetypeId];
		if (!TryResolveAccessorColumnIndex(
				archetype,
				location.ArchetypeId,
				typeId,
				ref cachedArchetypeId,
				ref cachedColumnIndex
			))
			throw new KeyNotFoundException(
				$"Component '{componentType.Name}' was not found for entity '{entity.Id}:{entity.Version}'."
			);

		chunkIndex = location.ChunkIndex;
		rowIndex   = location.RowIndex;
	}

	public void SetComponentBoxed(Entity entity, Type componentType, object value)
	{
		if (componentType is null) throw new ArgumentNullException(nameof(componentType));
		if (value is null) throw new ArgumentNullException(nameof(value));
		if (!componentType.IsValueType)
			throw new ArgumentException("Components must be value types.", nameof(componentType));

		EnsureAlive(entity);
		int typeId = GetOrCreateComponentTypeId(componentType, _world.ContainsReferencesForEntityStore(componentType));
		SetComponentInternalBoxed(entity.Id, typeId, componentType, value);
	}

	public void SetRelationComponent(Entity source, int relationTypeId)
	{
		var marker = default(RelationMarker);
		SetComponentInternal(source.Id, relationTypeId, in marker);
	}

	private static bool IsEntityMarked(uint[] markerBits, int entityId)
	{
		int  markerWordIndex = entityId >> 5;
		uint markerBit       = 1u << (entityId & 31);
		return (markerBits[markerWordIndex] & markerBit) != 0u;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool TryResolveAccessorColumnIndex(
		ArchetypeStorage archetype,
		int              archetypeId,
		int              typeId,
		ref int          cachedArchetypeId,
		ref int          cachedColumnIndex)
	{
		if (cachedArchetypeId == archetypeId)
			return cachedColumnIndex >= 0;

		cachedArchetypeId = archetypeId;
		int columnIndex = archetype.GetColumnIndexOrNegative(typeId);
		if (columnIndex >= 0)
		{
			cachedColumnIndex = columnIndex;
			return true;
		}

		cachedColumnIndex = -1;
		return false;
	}

	private static int GetMaskWordCount(int componentTypeCapacity) =>
		Math.Max(1, (componentTypeCapacity + 63) / 64);

	private static int[] AddTypeIdSorted(int[] source, int typeId)
	{
		var result   = new int[source.Length + 1];
		var src      = 0;
		var dst      = 0;
		var inserted = false;
		while (src < source.Length)
		{
			if (!inserted && typeId < source[src])
			{
				result[dst++] = typeId;
				inserted      = true;
			}

			result[dst++] = source[src++];
		}

		if (!inserted)
			result[dst] = typeId;

		return result;
	}

	private static int[] RemoveTypeIdSorted(int[] source, int typeId)
	{
		var result  = new int[source.Length - 1];
		var dst     = 0;
		var removed = false;
		for (var i = 0; i < source.Length; i++)
		{
			if (!removed && source[i] == typeId)
			{
				removed = true;
				continue;
			}

			if (dst < result.Length)
				result[dst++] = source[i];
		}

		if (!removed)
			throw new InvalidOperationException($"Type id '{typeId}' was not found in source archetype.");

		return result;
	}

	private bool IsChunkFullyMarked(uint[] markerBits, ArchetypeStorage.Chunk chunk)
	{
		for (var row = 0; row < chunk.Count; row++)
		{
			int entityId = chunk.EntityIds[row];
			if (unchecked((uint)entityId) >= unchecked((uint)_config.EntityCapacity) || !IsEntityMarked(markerBits, entityId))
				return false;
		}

		return true;
	}

	private int GetOrCreateAddTransition(ArchetypeStorage sourceArchetype, int typeId)
	{
		int cached = sourceArchetype.GetKnownAddTransition(typeId);
		if (cached != int.MinValue)
			return cached;

		if (sourceArchetype.HasType(typeId))
		{
			sourceArchetype.SetKnownAddTransition(typeId, sourceArchetype.Id);
			return sourceArchetype.Id;
		}

		int target = GetOrCreateArchetype(AddTypeIdSorted(sourceArchetype.TypeIds, typeId));
		sourceArchetype.SetKnownAddTransition(typeId, target);
		Archetypes[target].SetKnownRemoveTransition(typeId, sourceArchetype.Id);
		return target;
	}

	private int GetOrCreateArchetype(int[] sortedTypeIds)
	{
		var key = new ArchetypeTypeSetKey(sortedTypeIds);
		if (ArchetypeByTypeSet.TryGetValue(key, out int existing))
			return existing;

		var columnTypes = new Type[sortedTypeIds.Length];
		var managedLane = new bool[sortedTypeIds.Length];
		for (var i = 0; i < sortedTypeIds.Length; i++)
		{
			int typeId = sortedTypeIds[i];
			columnTypes[i] = TypeById[typeId] ??
							 throw new InvalidOperationException(
								 $"Component type id '{typeId}' was not registered before archetype creation."
							 );

			managedLane[i] = TypeIsManagedLane[typeId];
		}

		int archetypeId = Archetypes.Count;
		var archetype = new ArchetypeStorage(
			archetypeId,
			sortedTypeIds,
			BuildMaskWords(sortedTypeIds),
			columnTypes,
			managedLane,
			_config.ComponentTypeCapacity,
			_config.ChunkCapacity
		);

		Archetypes.Add(archetype);
		ArchetypeByTypeSet[key] = archetypeId;
		ArchetypeVersion++;
		return archetypeId;
	}

	private int GetOrCreateRemoveTransition(ArchetypeStorage sourceArchetype, int typeId)
	{
		int cached = sourceArchetype.GetKnownRemoveTransition(typeId);
		if (cached != int.MinValue)
			return cached;

		if (!sourceArchetype.HasType(typeId))
		{
			sourceArchetype.SetKnownRemoveTransition(typeId, sourceArchetype.Id);
			return sourceArchetype.Id;
		}

		int target = GetOrCreateArchetype(RemoveTypeIdSorted(sourceArchetype.TypeIds, typeId));
		sourceArchetype.SetKnownRemoveTransition(typeId, target);
		Archetypes[target].SetKnownAddTransition(typeId, sourceArchetype.Id);
		return target;
	}

	private int[] GetOrCreateTransitionCopyMap(ArchetypeStorage sourceArchetype, ArchetypeStorage targetArchetype)
	{
		long pairKey = (long)sourceArchetype.Id << 32 | unchecked((uint)targetArchetype.Id);
		if (TransitionCopyMapByPair.TryGetValue(pairKey, out int[]? existing))
			return existing;

		var sharedColumnCount = 0;
		for (var i = 0; i < targetArchetype.TypeIds.Length; i++)
		{
			int typeId = targetArchetype.TypeIds[i];
			if (sourceArchetype.GetColumnIndexOrNegative(typeId) >= 0)
				sharedColumnCount++;
		}

		var pairs     = new int[sharedColumnCount * 2];
		var pairIndex = 0;
		for (var i = 0; i < targetArchetype.TypeIds.Length; i++)
		{
			int typeId            = targetArchetype.TypeIds[i];
			int sourceColumnIndex = sourceArchetype.GetColumnIndexOrNegative(typeId);
			if (sourceColumnIndex < 0)
				continue;

			pairs[pairIndex++] = sourceColumnIndex;
			pairs[pairIndex++] = i;
		}

		TransitionCopyMapByPair[pairKey] = pairs;
		return pairs;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private ulong AdvanceComponentChangeVersion() =>
		_world.AdvanceComponentChangeVersionForEntityStore();

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void MarkComponentAdded(int entityId, int typeId, ulong version) =>
		_world.MarkComponentAddedForEntityStore(entityId, typeId, version);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void MarkComponentChanged(int entityId, int typeId, ulong version) =>
		_world.MarkComponentChangedForEntityStore(entityId, typeId, version);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void OverwriteComponent<T>(
		int                  entityId,
		Fixed.EntityLocation location,
		ArchetypeStorage     archetype,
		int                  typeId,
		in T                 component)
		where T : struct
	{
		int columnIndex = archetype.GetColumnIndexOrNegative(typeId);
		if (columnIndex < 0)
			throw new InvalidOperationException(
				$"Type id '{typeId}' does not exist in archetype '{archetype.Id}'."
			);

		var chunk = archetype.GetChunk(location.ChunkIndex);
		ref var existing = ref archetype.GetRefByIndex<T>(chunk, columnIndex, location.RowIndex);
		existing = component;
		ulong changeVersion = AdvanceComponentChangeVersion();
		archetype.MarkComponentChanged(chunk, columnIndex, changeVersion);
		MarkComponentChanged(entityId, typeId, changeVersion);
	}

	private void MoveEntireChunkToArchetype(
		ArchetypeStorage       sourceArchetype,
		int                    sourceChunkIndex,
		ArchetypeStorage.Chunk sourceChunk,
		ArchetypeStorage       targetArchetype,
		int[]                  sourceTargetColumnPairs)
	{
		int rowCount  = sourceChunk.Count;
		var sourceRow = 0;
		while (sourceRow < rowCount)
		{
			int reservedRows = targetArchetype.ReserveRows(
				rowCount - sourceRow,
				out int targetChunkIndex,
				out int targetRowStart
			);

			var targetChunk = targetArchetype.GetChunkUnchecked(targetChunkIndex);
			Array.Copy(
				sourceChunk.EntityIds,
				sourceRow,
				targetChunk.EntityIds,
				targetRowStart,
				reservedRows
			);

			targetArchetype.CopySharedColumnsFromWithPairs(
				sourceChunk,
				sourceRow,
				targetChunk,
				targetRowStart,
				reservedRows,
				sourceTargetColumnPairs
			);

			int sourceRowEnd = sourceRow + reservedRows;
			int targetRow    = targetRowStart;
			for (; sourceRow < sourceRowEnd; sourceRow++, targetRow++)
			{
				int entityId = sourceChunk.EntityIds[sourceRow];
				LocationByEntityId[entityId] = new(targetArchetype.Id, targetChunkIndex, targetRow);
			}
		}

		sourceArchetype.ClearChunk(sourceChunkIndex);
	}

	private void MoveEntityToArchetype(
		int                  entityId,
		Fixed.EntityLocation sourceLocation,
		ArchetypeStorage     sourceArchetype,
		int                  targetArchetypeId)
	{
		if (targetArchetypeId == sourceArchetype.Id)
			return;

		var   targetArchetype         = Archetypes[targetArchetypeId];
		int[] sourceTargetColumnPairs = GetOrCreateTransitionCopyMap(sourceArchetype, targetArchetype);
		MoveEntityToArchetype(
			entityId,
			sourceLocation,
			sourceArchetype,
			targetArchetype,
			sourceTargetColumnPairs
		);
	}

	private void MoveEntityToArchetype(
		int                  entityId,
		Fixed.EntityLocation sourceLocation,
		ArchetypeStorage     sourceArchetype,
		ArchetypeStorage     targetArchetype,
		int[]                sourceTargetColumnPairs)
	{
		var sourceChunk = sourceArchetype.GetChunk(sourceLocation.ChunkIndex);
		targetArchetype.AllocateRow(entityId, out int targetChunkIndex, out int targetRowIndex);
		var targetChunk = targetArchetype.GetChunk(targetChunkIndex);
		targetArchetype.CopySharedColumnsFromWithPairs(
			sourceChunk,
			sourceLocation.RowIndex,
			targetChunk,
			targetRowIndex,
			sourceTargetColumnPairs
		);

		LocationByEntityId[entityId] = new(targetArchetype.Id, targetChunkIndex, targetRowIndex);
		if (sourceArchetype.RemoveAt(sourceLocation.ChunkIndex, sourceLocation.RowIndex, out int movedEntityId))
			UpdateMovedEntityLocation(
				sourceArchetype.Id, sourceLocation.ChunkIndex, sourceLocation.RowIndex, movedEntityId
			);
	}

	private void MoveEntityToArchetypeWithSet<T>(
		int                  entityId,
		Fixed.EntityLocation sourceLocation,
		ArchetypeStorage     sourceArchetype,
		int                  targetArchetypeId,
		int                  setTypeId,
		in T                 setComponent)
		where T : struct
	{
		if (targetArchetypeId == sourceArchetype.Id)
		{
			OverwriteComponent(entityId, sourceLocation, sourceArchetype, setTypeId, in setComponent);
			return;
		}

		var   targetArchetype         = Archetypes[targetArchetypeId];
		int[] sourceTargetColumnPairs = GetOrCreateTransitionCopyMap(sourceArchetype, targetArchetype);
		MoveEntityToArchetypeWithSet(
			entityId,
			sourceLocation,
			sourceArchetype,
			targetArchetype,
			sourceTargetColumnPairs,
			setTypeId,
			in setComponent
		);
	}

	private void MoveEntityToArchetypeWithSet<T>(
		int                  entityId,
		Fixed.EntityLocation sourceLocation,
		ArchetypeStorage     sourceArchetype,
		ArchetypeStorage     targetArchetype,
		int[]                sourceTargetColumnPairs,
		int                  setTypeId,
		in T                 setComponent)
		where T : struct
	{
		bool setWasAdded = sourceArchetype.GetColumnIndexOrNegative(setTypeId) < 0;
		var  sourceChunk = sourceArchetype.GetChunk(sourceLocation.ChunkIndex);
		targetArchetype.AllocateRow(entityId, out int targetChunkIndex, out int targetRowIndex);
		var targetChunk = targetArchetype.GetChunk(targetChunkIndex);
		targetArchetype.CopySharedColumnsFromWithPairs(
			sourceChunk,
			sourceLocation.RowIndex,
			targetChunk,
			targetRowIndex,
			sourceTargetColumnPairs
		);

		ref var setRef = ref targetArchetype.GetRef<T>(targetChunkIndex, targetRowIndex, setTypeId);
		setRef = setComponent;
		int targetColumnIndex = targetArchetype.GetColumnIndexOrNegative(setTypeId);
		if (targetColumnIndex < 0)
			throw new InvalidOperationException(
				$"Type id '{setTypeId}' does not exist in archetype '{targetArchetype.Id}'."
			);

		ulong changeVersion = AdvanceComponentChangeVersion();
		targetArchetype.MarkComponentChanged(targetChunk, targetColumnIndex, changeVersion);
		MarkComponentChanged(entityId, setTypeId, changeVersion);
		if (setWasAdded)
			MarkComponentAdded(entityId, setTypeId, changeVersion);

		LocationByEntityId[entityId] = new(targetArchetype.Id, targetChunkIndex, targetRowIndex);

		if (sourceArchetype.RemoveAt(sourceLocation.ChunkIndex, sourceLocation.RowIndex, out int movedEntityId))
			UpdateMovedEntityLocation(
				sourceArchetype.Id, sourceLocation.ChunkIndex, sourceLocation.RowIndex, movedEntityId
			);
	}

	private void MoveEntityToArchetypeWithSetBoxed(
		int                  entityId,
		Fixed.EntityLocation sourceLocation,
		ArchetypeStorage     sourceArchetype,
		int                  targetArchetypeId,
		int                  setTypeId,
		Type                 setComponentType,
		object               setComponent)
	{
		if (targetArchetypeId == sourceArchetype.Id)
		{
			int sourceColumnIndex = sourceArchetype.GetColumnIndexOrNegative(setTypeId);
			if (sourceColumnIndex < 0)
				throw new InvalidOperationException(
					$"Type id '{setTypeId}' does not exist in archetype '{sourceArchetype.Id}'."
				);

			var sourceChunk = sourceArchetype.GetChunkUnchecked(sourceLocation.ChunkIndex);
			sourceChunk.Columns[sourceColumnIndex].SetValue(sourceLocation.RowIndex, setComponent);
			ulong changeVersion = AdvanceComponentChangeVersion();
			sourceArchetype.MarkComponentChanged(sourceChunk, sourceColumnIndex, changeVersion);
			MarkComponentChanged(entityId, setTypeId, changeVersion);
			return;
		}

		bool  setWasAdded             = sourceArchetype.GetColumnIndexOrNegative(setTypeId) < 0;
		var   sourceChunkForMove      = sourceArchetype.GetChunk(sourceLocation.ChunkIndex);
		var   targetArchetype         = Archetypes[targetArchetypeId];
		int[] sourceTargetColumnPairs = GetOrCreateTransitionCopyMap(sourceArchetype, targetArchetype);
		targetArchetype.AllocateRow(entityId, out int targetChunkIndex, out int targetRowIndex);
		var targetChunk = targetArchetype.GetChunk(targetChunkIndex);
		targetArchetype.CopySharedColumnsFromWithPairs(
			sourceChunkForMove,
			sourceLocation.RowIndex,
			targetChunk,
			targetRowIndex,
			sourceTargetColumnPairs
		);

		int targetColumnIndex = targetArchetype.GetColumnIndexOrNegative(setTypeId);
		if (targetColumnIndex < 0)
			throw new InvalidOperationException(
				$"Type id '{setTypeId}' does not exist in archetype '{targetArchetype.Id}'."
			);

		// TODO: Cache boxed snapshot set delegates by component type to reduce reflection-heavy restore overhead.
		if (targetChunk.Columns[targetColumnIndex].ComponentType != setComponentType)
			throw new InvalidOperationException(
				$"Snapshot component type '{setComponentType.FullName}' does not match runtime column type '{targetChunk.Columns[targetColumnIndex].ComponentType.FullName}'."
			);

		targetChunk.Columns[targetColumnIndex].SetValue(targetRowIndex, setComponent);
		ulong targetChangeVersion = AdvanceComponentChangeVersion();
		targetArchetype.MarkComponentChanged(targetChunk, targetColumnIndex, targetChangeVersion);
		MarkComponentChanged(entityId, setTypeId, targetChangeVersion);
		if (setWasAdded)
			MarkComponentAdded(entityId, setTypeId, targetChangeVersion);

		LocationByEntityId[entityId] = new(targetArchetype.Id, targetChunkIndex, targetRowIndex);
		if (sourceArchetype.RemoveAt(sourceLocation.ChunkIndex, sourceLocation.RowIndex, out int movedEntityId))
			UpdateMovedEntityLocation(
				sourceArchetype.Id, sourceLocation.ChunkIndex, sourceLocation.RowIndex, movedEntityId
			);
	}

	private void RemoveRelationTypeFromAllSources(int relationTypeId)
	{
		if (relationTypeId < 0)
			return;

		for (var entityId = 0; entityId < NextEntityId; entityId++)
		{
			if (!AliveByEntityId[entityId])
				continue;

			var location = LocationByEntityId[entityId];
			if (!location.IsValid)
				continue;

			var archetype = Archetypes[location.ArchetypeId];
			if (!archetype.HasType(relationTypeId))
				continue;

			RemoveComponentFromCommand(new(entityId, VersionByEntityId[entityId]), relationTypeId);
		}
	}

	private void SetComponentInternal<T>(int entityId, int typeId, in T component) where T : struct
	{
		var location = LocationByEntityId[entityId];
		if (!location.IsValid)
			throw new InvalidOperationException($"Entity '{entityId}' is not in a valid archetype.");

		var sourceArchetype = Archetypes[location.ArchetypeId];
		if (sourceArchetype.HasType(typeId))
		{
			OverwriteComponent(entityId, location, sourceArchetype, typeId, in component);
			return;
		}

		_world.EnsureStructuralChangesAllowed();
		int targetArchetypeId = GetOrCreateAddTransition(sourceArchetype, typeId);
		MoveEntityToArchetypeWithSet(entityId, location, sourceArchetype, targetArchetypeId, typeId, in component);
	}

	private void SetComponentInternalBoxed(
		int    entityId,
		int    typeId,
		Type   componentType,
		object component)
	{
		var location = LocationByEntityId[entityId];
		if (!location.IsValid)
			throw new InvalidOperationException($"Entity '{entityId}' is not in a valid archetype.");

		var sourceArchetype = Archetypes[location.ArchetypeId];
		if (sourceArchetype.HasType(typeId))
		{
			int sourceColumnIndex = sourceArchetype.GetColumnIndexOrNegative(typeId);
			if (sourceColumnIndex < 0)
				throw new InvalidOperationException(
					$"Type id '{typeId}' does not exist in archetype '{sourceArchetype.Id}'."
				);

			var sourceChunk = sourceArchetype.GetChunkUnchecked(location.ChunkIndex);
			sourceChunk.Columns[sourceColumnIndex].SetValue(location.RowIndex, component);
			ulong changeVersion = AdvanceComponentChangeVersion();
			sourceArchetype.MarkComponentChanged(sourceChunk, sourceColumnIndex, changeVersion);
			MarkComponentChanged(entityId, typeId, changeVersion);
			return;
		}

		int targetArchetypeId = GetOrCreateAddTransition(sourceArchetype, typeId);
		MoveEntityToArchetypeWithSetBoxed(
			entityId,
			location,
			sourceArchetype,
			targetArchetypeId,
			typeId,
			componentType,
			component
		);
	}

	private void UpdateMovedEntityLocation(int archetypeId, int chunkIndex, int rowIndex, int movedEntityId)
	{
		var moved = LocationByEntityId[movedEntityId];
		LocationByEntityId[movedEntityId] = new(archetypeId, chunkIndex, rowIndex);
		if (moved.ArchetypeId != archetypeId || moved.ChunkIndex != chunkIndex)
			throw new InvalidOperationException("Moved entity location update is inconsistent.");
	}
}

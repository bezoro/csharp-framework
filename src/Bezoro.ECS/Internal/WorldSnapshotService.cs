using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Options;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Internal;

internal sealed class WorldSnapshotService(
	World                 world,
	WorldConfig           config,
	WorldEntityStore      entityStore,
	WorldResourceStore    resourceStore,
	WorldRelationIndex    relationIndex,
	Type?[]               typeById,
	IReadOnlyDictionary<Type, int> typeToId
)
{
	private readonly IReadOnlyDictionary<Type, int> _typeToId      = typeToId;
	private readonly Type?[]               _typeById      = typeById;
	private readonly World                 _world         = world;
	private readonly WorldConfig           _config        = config;
	private readonly WorldEntityStore      _entityStore   = entityStore;
	private readonly WorldRelationIndex    _relationIndex = relationIndex;
	private readonly WorldResourceStore    _resourceStore = resourceStore;

	public SnapshotRestorePlan ValidateRestorePlan(
		WorldSnapshot                  snapshot,
		SnapshotDeserializationOptions options)
	{
		var snapshotEntities      = snapshot.Entities;
		var snapshotResources     = snapshot.Resources;
		var snapshotRelations     = snapshot.Relations;
		var entityIds             = new HashSet<Entity>(snapshotEntities.Length);
		var resourceTypes         = new HashSet<Type>();
		var missingComponentTypes = new HashSet<Type>();
		var relationMarkerTypes   = new HashSet<(Type relationType, Entity target)>();
		var relationTriples       = new HashSet<(Type relationType, Entity source, Entity target)>();

		if (snapshotEntities.Length > _config.EntityCapacity)
			throw new InvalidOperationException(
				$"Snapshot entity capacity '{snapshotEntities.Length}' exceeds configured entity capacity '{_config.EntityCapacity}'."
			);

		for (var i = 0; i < snapshotResources.Length; i++)
		{
			var resource = snapshotResources[i];
			ValidateSnapshotResource(resource, options);
			if (!resourceTypes.Add(resource.ResourceType))
				throw new InvalidOperationException(
					$"Duplicate snapshot resource type '{resource.ResourceType.FullName}' is not allowed."
				);
		}

		for (var i = 0; i < snapshotEntities.Length; i++)
		{
			var captured = snapshotEntities[i];
			if (captured.Entity == Entity.None)
				throw new InvalidOperationException("Snapshot entities cannot use Entity.None as an identity.");

			if (!entityIds.Add(captured.Entity))
				throw new InvalidOperationException(
					$"Duplicate snapshot entity '{captured.Entity.Id}:{captured.Entity.Version}' is not allowed."
				);

			if (captured.Components is null)
				throw new InvalidOperationException(
					$"Snapshot entity '{captured.Entity.Id}:{captured.Entity.Version}' has a null component array."
				);

			var entityComponentTypes = new HashSet<Type>();
			for (var componentIndex = 0; componentIndex < captured.Components.Length; componentIndex++)
			{
				var component = captured.Components[componentIndex];
				ValidateSnapshotComponent(component, options);
				if (!entityComponentTypes.Add(component.ComponentType))
					throw new InvalidOperationException(
						$"Duplicate snapshot component type '{component.ComponentType.FullName}' for entity '{captured.Entity.Id}:{captured.Entity.Version}' is not allowed."
					);

				if (!_typeToId.ContainsKey(component.ComponentType))
					missingComponentTypes.Add(component.ComponentType);
			}
		}

		for (var i = 0; i < snapshotRelations.Length; i++)
		{
			var relation = snapshotRelations[i];
			ValidateSnapshotRelation(relation, options);
			if (!entityIds.Contains(relation.Source))
				throw new InvalidOperationException(
					$"Snapshot relation source '{relation.Source.Id}:{relation.Source.Version}' was not found in the entity payload."
				);

			if (!entityIds.Contains(relation.Target))
				throw new InvalidOperationException(
					$"Snapshot relation target '{relation.Target.Id}:{relation.Target.Version}' was not found in the entity payload."
				);

			if (!relationTriples.Add((relation.RelationType, relation.Source, relation.Target)))
				throw new InvalidOperationException(
					$"Duplicate snapshot relation '{relation.RelationType.FullName}' from '{relation.Source.Id}:{relation.Source.Version}' to '{relation.Target.Id}:{relation.Target.Version}' is not allowed."
				);

			relationMarkerTypes.Add((relation.RelationType, relation.Target));
		}

		missingComponentTypes.RemoveWhere(_typeToId.ContainsKey);
		int projectedTypeCount = checked(_entityStore.TypeCount + missingComponentTypes.Count + relationMarkerTypes.Count);
		if (projectedTypeCount > _config.ComponentTypeCapacity)
			throw new InvalidOperationException(
				$"Snapshot restore would exceed component type capacity '{_config.ComponentTypeCapacity}'."
			);

		return new(snapshotResources, snapshotEntities, snapshotRelations);
	}

	public void ApplyRestorePlan(in SnapshotRestorePlan restorePlan)
	{
		var entityMap = new Dictionary<Entity, Entity>(restorePlan.Entities.Length);
		for (var i = 0; i < restorePlan.Entities.Length; i++)
		{
			var captured = restorePlan.Entities[i];
			entityMap[captured.Entity] = _world.Spawn();
		}

		for (var i = 0; i < restorePlan.Resources.Length; i++)
		{
			var resource = restorePlan.Resources[i];
			_world.SetResourceBoxedFromSnapshot(resource.ResourceType, resource.Value);
		}

		for (var i = 0; i < restorePlan.Entities.Length; i++)
		{
			var captured = restorePlan.Entities[i];
			if (!entityMap.TryGetValue(captured.Entity, out var restored))
				throw new InvalidOperationException(
					$"Snapshot entity '{captured.Entity.Id}:{captured.Entity.Version}' was not restored."
				);

			for (var componentIndex = 0; componentIndex < captured.Components.Length; componentIndex++)
			{
				var component = captured.Components[componentIndex];
				_world.SetComponentFromSnapshot(restored, component.ComponentType, component.Value);
			}
		}

		for (var i = 0; i < restorePlan.Relations.Length; i++)
		{
			var relation = restorePlan.Relations[i];
			if (!entityMap.TryGetValue(relation.Source, out var source))
				throw new InvalidOperationException(
					$"Snapshot relation source '{relation.Source.Id}:{relation.Source.Version}' was not restored."
				);

			if (!entityMap.TryGetValue(relation.Target, out var target))
				throw new InvalidOperationException(
					$"Snapshot relation target '{relation.Target.Id}:{relation.Target.Version}' was not restored."
				);

			_world.AddRelationFromSnapshot(relation.RelationType, source, target);
		}
	}

	public void Capture<TSnapshotWriter>(ref TSnapshotWriter writer, int aliveCount, int nextEntityId)
		where TSnapshotWriter : struct, IWorldSnapshotWriter
	{
		var resources = _resourceStore.CaptureSnapshotRecords();

		var entities  = new List<SnapshotEntityRecord>(aliveCount);
		var relations = new List<SnapshotRelationRecord>();
		for (var entityId = 0; entityId < nextEntityId; entityId++)
		{
			if (!_entityStore.AliveByEntityId[entityId])
				continue;

			var entity   = new Entity(entityId, _entityStore.VersionByEntityId[entityId]);
			var location = _entityStore.LocationByEntityId[entityId];
			if (!location.IsValid)
				continue;

			var archetype  = _entityStore.Archetypes[location.ArchetypeId];
			var chunk      = archetype.GetChunkUnchecked(location.ChunkIndex);
			var components = new List<SnapshotComponentRecord>(archetype.TypeIds.Length);
			for (var typeIndex = 0; typeIndex < archetype.TypeIds.Length; typeIndex++)
			{
				int typeId = archetype.TypeIds[typeIndex];
				if (_relationIndex.TryGetRelationInfo(typeId, out var relationInfo))
				{
					relations.Add(new(relationInfo.RelationType, entity, relationInfo.Target));
					continue;
				}

				var componentType = _typeById[typeId] ??
									throw new InvalidOperationException(
										$"Component type id '{typeId}' is not registered."
									);

				object component = chunk.Columns[typeIndex].GetValue(location.RowIndex);
				components.Add(new(componentType, component));
			}

			entities.Add(new(entity, [.. components]));
		}

		var snapshot = new WorldSnapshot(resources, [.. entities], [.. relations]);
		writer.Write(in snapshot);
	}

	private static void ValidateSnapshotComponent(
		in SnapshotComponentRecord     component,
		SnapshotDeserializationOptions options)
	{
		if (component.ComponentType is null)
			throw new InvalidOperationException("Snapshot component type cannot be null.");

		if (!component.ComponentType.IsValueType)
			throw new InvalidOperationException(
				$"Snapshot component type '{component.ComponentType.FullName}' must be a value type."
			);

		if (component.Value is null)
			throw new InvalidOperationException(
				$"Snapshot component '{component.ComponentType.FullName}' contains a null value."
			);

		if (component.Value.GetType() != component.ComponentType)
			throw new InvalidOperationException(
				$"Snapshot component value type '{component.Value.GetType().FullName}' does not match declared type '{component.ComponentType.FullName}'."
			);

		if (!options.IsComponentTypeAllowListed(component.ComponentType))
			throw new InvalidOperationException(
				$"Snapshot component type '{component.ComponentType.FullName}' is not allow-listed."
			);

		if (!options.IsTypeAllowed(component.ComponentType))
			throw new InvalidOperationException(
				$"Snapshot component type '{component.ComponentType.FullName}' is not allowed."
			);
	}

	private static void ValidateSnapshotRelation(
		in SnapshotRelationRecord      relation,
		SnapshotDeserializationOptions options)
	{
		if (relation.RelationType is null)
			throw new InvalidOperationException("Snapshot relation type cannot be null.");

		if (!relation.RelationType.IsValueType)
			throw new InvalidOperationException(
				$"Snapshot relation type '{relation.RelationType.FullName}' must be a value type."
			);

		if (relation.Source == Entity.None ||
			relation.Source == Entity.Wildcard ||
			relation.Target == Entity.None ||
			relation.Target == Entity.Wildcard)
			throw new InvalidOperationException("Snapshot relations must reference concrete entities.");

		if (!options.IsRelationTypeAllowListed(relation.RelationType))
			throw new InvalidOperationException(
				$"Snapshot relation type '{relation.RelationType.FullName}' is not allow-listed."
			);

		if (!options.IsTypeAllowed(relation.RelationType))
			throw new InvalidOperationException(
				$"Snapshot relation type '{relation.RelationType.FullName}' is not allowed."
			);
	}

	private static void ValidateSnapshotResource(
		in SnapshotResourceRecord      resource,
		SnapshotDeserializationOptions options)
	{
		if (resource.ResourceType is null)
			throw new InvalidOperationException("Snapshot resource type cannot be null.");

		if (resource.Value is null)
			throw new InvalidOperationException(
				$"Snapshot resource '{resource.ResourceType.FullName}' contains a null value."
			);

		if (resource.Value.GetType() != resource.ResourceType)
			throw new InvalidOperationException(
				$"Snapshot resource value type '{resource.Value.GetType().FullName}' does not match declared type '{resource.ResourceType.FullName}'."
			);

		if (!options.IsResourceTypeAllowListed(resource.ResourceType))
			throw new InvalidOperationException(
				$"Snapshot resource type '{resource.ResourceType.FullName}' is not allow-listed."
			);

		if (!options.IsTypeAllowed(resource.ResourceType))
			throw new InvalidOperationException(
				$"Snapshot resource type '{resource.ResourceType.FullName}' is not allowed."
			);
	}
}

namespace Bezoro.ECS.Types;

/// <summary>
///     Snapshot payload containing resources, entities/components, and relations.
/// </summary>
/// <remarks>
///     This mutable transport DTO retains the supplied arrays. Captured reference objects are shared with their
///     source values until a serializer materializes independent objects. Restore transfers resource ownership to
///     the destination world; do not restore the same disposable resource instance into multiple owners.
/// </remarks>
/// <param name="resources">Serialized resources.</param>
/// <param name="entities">Serialized entity/component payloads.</param>
/// <param name="relations">Serialized relation edges.</param>
public sealed class WorldSnapshot(
	SnapshotResourceRecord[] resources,
	SnapshotEntityRecord[] entities,
	SnapshotRelationRecord[] relations)
{
	/// <summary>
	///     Serialized entity/component payloads.
	/// </summary>
	public SnapshotEntityRecord[] Entities { get; } = entities ?? throw new ArgumentNullException(nameof(entities));

	/// <summary>
	///     Serialized relation edges.
	/// </summary>
	public SnapshotRelationRecord[] Relations { get; } = relations ??
	                                                    throw new ArgumentNullException(nameof(relations));

	/// <summary>
	///     Serialized resources.
	/// </summary>
	public SnapshotResourceRecord[] Resources { get; } = resources ??
	                                                    throw new ArgumentNullException(nameof(resources));
}

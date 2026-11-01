using Bezoro.ECS.Services;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Internal.Fixed;

internal sealed class CompiledQueryPlan(
	World   owner,
	int[]   allTypeIds,
	int[]   noneTypeIds,
	int[]   anyTypeIds,
	int[]   optionalTypeIds,
	int[]   addedTypeIds,
	int[]   changedTypeIds,
	ulong[] allMaskWords,
	ulong[] noneMaskWords,
	ulong[] anyMaskWords,
	int[]   allMaskWordIndices,
	int[]   noneMaskWordIndices,
	int[]   anyMaskWordIndices,
	Type?   relatedRelationType,
	Entity  relatedTarget
)
{
	private int   _matchingArchetypeCount;
	private int[] _matchingArchetypeIds = [];

	public int[] AllMaskWordIndices { get; } = allMaskWordIndices;

	public int[] AllTypeIds { get; } = allTypeIds;

	public int[] AnyMaskWordIndices { get; } = anyMaskWordIndices;

	public int[] AnyTypeIds { get; } = anyTypeIds;

	public int[] AddedTypeIds { get; } = addedTypeIds;

	public int[] ChangedTypeIds { get; } = changedTypeIds;

	public int[] NoneMaskWordIndices { get; } = noneMaskWordIndices;

	public int[] NoneTypeIds { get; } = noneTypeIds;

	public int[] OptionalTypeIds { get; } = optionalTypeIds;

	public ulong[] AllMaskWords { get; } = allMaskWords;

	public ulong[] AnyMaskWords { get; } = anyMaskWords;

	public ulong[] NoneMaskWords { get; } = noneMaskWords;

	public World Owner { get; } = owner;

	public Type? RelatedRelationType { get; } = relatedRelationType;

	public Entity RelatedTarget { get; } = relatedTarget;

	public int ArchetypeCacheVersion { get; set; } = -1;

	public ulong LastObservedChangeVersion { get; set; }

	public int MatchingArchetypeCount
	{
		get => _matchingArchetypeCount;
		set => _matchingArchetypeCount = value < 0 ? 0 : value;
	}

	public int[] MatchingArchetypeIds
	{
		get => _matchingArchetypeIds;
		set => _matchingArchetypeIds = value ?? [];
	}
}

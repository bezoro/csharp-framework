using System.Runtime.CompilerServices;
using Bezoro.ECS.Services;

namespace Bezoro.ECS.Types;

/// <summary>
///     Provides cached, type-stable component access for hot sequential entity loops.
/// </summary>
/// <typeparam name="T">Unmanaged component type.</typeparam>
public struct ComponentAccessor<T>
	where T : unmanaged
{
	private readonly int   _typeId;
	private readonly World _world;
	private          int   _cachedArchetypeId;
	private          int   _cachedColumnIndex;

	internal ComponentAccessor(World world, int typeId)
	{
		_world             = world ?? throw new ArgumentNullException(nameof(world));
		_typeId            = typeId;
		_cachedArchetypeId = -1;
		_cachedColumnIndex = -1;
	}

	/// <summary>
	///     Determines whether the specified entity contains <typeparamref name="T" />.
	/// </summary>
	/// <param name="entity">Entity to probe.</param>
	/// <returns><c>true</c> when the entity is alive and contains the component; otherwise <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool Has(Entity entity) =>
		_world.HasComponentForAccessor(
			entity,
			_typeId,
			ref _cachedArchetypeId,
			ref _cachedColumnIndex
		);

	/// <summary>
	///     Tries to get a component value for the specified entity.
	/// </summary>
	/// <param name="entity">Entity to probe.</param>
	/// <param name="component">Resolved component value when found.</param>
	/// <returns><c>true</c> when the entity is alive and contains <typeparamref name="T" />; otherwise <c>false</c>.</returns>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool TryRead(Entity entity, out T component) =>
		_world.TryGetComponentForAccessor(
			entity,
			_typeId,
			ref _cachedArchetypeId,
			ref _cachedColumnIndex,
			out component
		);

	/// <summary>
	///     Gets a mutable component reference for the specified entity.
	/// </summary>
	/// <param name="entity">Entity to read/write.</param>
	/// <returns>Mutable component reference.</returns>
	/// <exception cref="InvalidOperationException">Thrown when the entity is not alive.</exception>
	/// <exception cref="KeyNotFoundException">Thrown when the entity does not contain <typeparamref name="T" />.</exception>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public ref T Write(Entity entity)
	{
		_world.ResolveAccessorLocation(
			entity,
			_typeId,
			typeof(T),
			ref _cachedArchetypeId,
			ref _cachedColumnIndex,
			out var archetype,
			out int chunkIndex,
			out int rowIndex
		);

		var chunk = archetype.GetChunkUnchecked(chunkIndex);
		_world.TrackPotentialAccessorRefWrite(chunk, _cachedColumnIndex, _typeId, rowIndex);
		return ref archetype.GetRefByIndex<T>(chunk, _cachedColumnIndex, rowIndex);
	}
}

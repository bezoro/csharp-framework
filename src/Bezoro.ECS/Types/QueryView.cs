using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Internal;
using Bezoro.ECS.Services;

namespace Bezoro.ECS.Types;

/// <summary>
///     Canonical query surface for typed traversal and unmanaged struct-job execution.
/// </summary>
/// <typeparam name="TQuery">Compiled query specification type.</typeparam>
/// <param name="world">World that owns the view.</param>
/// <param name="handle">Compiled query handle used by every job execution.</param>
/// <exception cref="ArgumentNullException"><paramref name="world" /> is <see langword="null" />.</exception>
public readonly struct QueryView<TQuery>(World world, QueryHandle<TQuery> handle)
	where TQuery : struct, ICompiledQuerySpec
{
	/// <summary>
	///     Delegate invoked for each matching entity.
	/// </summary>
	/// <param name="entity">Matching entity.</param>
	public delegate void EntityAction(Entity entity);

	/// <summary>
	///     Delegate invoked for each matching entity with one read-only component.
	/// </summary>
	/// <typeparam name="T1">Read-only component type.</typeparam>
	/// <param name="entity">Matching entity.</param>
	/// <param name="component1">Read-only component.</param>
	public delegate void EntityInAction<T1>(Entity entity, in T1 component1)
		where T1 : struct;

	/// <summary>
	///     Delegate invoked for each matching entity with one mutable component.
	/// </summary>
	/// <typeparam name="T1">Mutable component type.</typeparam>
	/// <param name="entity">Matching entity.</param>
	/// <param name="component1">Mutable component.</param>
	public delegate void EntityRefAction<T1>(Entity entity, ref T1 component1)
		where T1 : struct;

	/// <summary>
	///     Delegate invoked for each matching entity with one mutable and one read-only component.
	/// </summary>
	/// <typeparam name="T1">Mutable component type.</typeparam>
	/// <typeparam name="T2">Read-only component type.</typeparam>
	/// <param name="entity">Matching entity.</param>
	/// <param name="component1">Mutable component.</param>
	/// <param name="component2">Read-only component.</param>
	public delegate void EntityRefInAction<T1, T2>(Entity entity, ref T1 component1, in T2 component2)
		where T1 : struct
		where T2 : struct;

	/// <summary>
	///     Delegate invoked for each matching entity with one mutable and two read-only components.
	/// </summary>
	/// <typeparam name="T1">Mutable component type.</typeparam>
	/// <typeparam name="T2">First read-only component type.</typeparam>
	/// <typeparam name="T3">Second read-only component type.</typeparam>
	/// <param name="entity">Matching entity.</param>
	/// <param name="component1">Mutable component.</param>
	/// <param name="component2">First read-only component.</param>
	/// <param name="component3">Second read-only component.</param>
	public delegate void EntityRefInAction<T1, T2, T3>(Entity entity, ref T1 component1, in T2 component2, in T3 component3)
		where T1 : struct
		where T2 : struct
		where T3 : struct;

	/// <summary>
	///     Delegate invoked for each matching entity with one mutable and three read-only components.
	/// </summary>
	/// <typeparam name="T1">Mutable component type.</typeparam>
	/// <typeparam name="T2">First read-only component type.</typeparam>
	/// <typeparam name="T3">Second read-only component type.</typeparam>
	/// <typeparam name="T4">Third read-only component type.</typeparam>
	/// <param name="entity">Matching entity.</param>
	/// <param name="component1">Mutable component.</param>
	/// <param name="component2">First read-only component.</param>
	/// <param name="component3">Second read-only component.</param>
	/// <param name="component4">Third read-only component.</param>
	public delegate void EntityRefInAction<T1, T2, T3, T4>(
		Entity entity,
		ref T1 component1,
		in T2 component2,
		in T3 component3,
		in T4 component4)
		where T1 : struct
		where T2 : struct
		where T3 : struct
		where T4 : struct;

	private readonly QueryHandle<TQuery> _handle = handle;
	private readonly World               _world  = world ?? throw new ArgumentNullException(nameof(world));

	private readonly struct ReadOnlyEntityAction<T1>(EntityInAction<T1> action) : IEntityChunkAction<T1>
		where T1 : struct
	{
		public void Invoke(Entity entity, ref T1 component1) => action(entity, in component1);
	}

	private readonly struct EntityAction<T1>(EntityRefAction<T1> action) : IEntityChunkAction<T1>
		where T1 : struct
	{
		public void Invoke(Entity entity, ref T1 component1) => action(entity, ref component1);
	}

	private readonly struct EntityAction<T1, T2>(EntityRefInAction<T1, T2> action) : IEntityChunkAction<T1, T2>
		where T1 : struct
		where T2 : struct
	{
		public void Invoke(Entity entity, ref T1 component1, in T2 component2) => action(entity, ref component1, in component2);
	}

	private readonly struct EntityAction<T1, T2, T3>(EntityRefInAction<T1, T2, T3> action) : IEntityChunkAction<T1, T2, T3>
		where T1 : struct
		where T2 : struct
		where T3 : struct
	{
		public void Invoke(Entity entity, ref T1 component1, in T2 component2, in T3 component3) => action(entity, ref component1, in component2, in component3);
	}

	private readonly struct EntityAction<T1, T2, T3, T4>(EntityRefInAction<T1, T2, T3, T4> action) : IEntityChunkAction<T1, T2, T3, T4>
		where T1 : struct
		where T2 : struct
		where T3 : struct
		where T4 : struct
	{
		public void Invoke(Entity entity, ref T1 component1, in T2 component2, in T3 component3, in T4 component4) => action(entity, ref component1, in component2, in component3, in component4);
	}

	/// <summary>
	///     Returns <c>true</c> when any entity matches the query.
	/// </summary>
	public bool Any()
	{
		using var cursor = _world.Execute(_handle);
		return cursor.Count > 0;
	}

	/// <summary>
	///     Counts matching entities.
	/// </summary>
	public int Count()
	{
		using var cursor = _world.Execute(_handle);
		return cursor.Count;
	}

	/// <summary>
	///     Executes an entity-only loop over the current query results.
	/// </summary>
	/// <param name="action">Callback invoked for each matching entity.</param>
	/// <exception cref="ArgumentNullException"><paramref name="action" /> is <see langword="null" />.</exception>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">
	///     The query handle belongs to a different world, or fail-fast query-result capacity is exceeded.
	/// </exception>
	/// <remarks>The callback is validated before world lifetime and query-handle ownership.</remarks>
	public void ForEach(EntityAction action)
	{
		if (action is null) throw new ArgumentNullException(nameof(action));

		using var cursor = _world.Execute(_handle);
		if (!cursor.MoveNext())
			return;

		var entities = cursor.Current;
		for (var i = 0; i < entities.Length; i++)
			action(entities[i]);
	}

	/// <summary>
	///     Executes an entity-aware read-only loop over one component.
	/// </summary>
	/// <typeparam name="T1">Read-only component type.</typeparam>
	/// <param name="action">Callback invoked for each matching entity and component.</param>
	/// <exception cref="ArgumentNullException"><paramref name="action" /> is <see langword="null" />.</exception>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">
	///     The query handle belongs to a different world, or fail-fast query-result capacity is exceeded.
	/// </exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain the requested component.</exception>
	/// <remarks>The callback is validated before world lifetime and query-handle ownership.</remarks>
	public void ForEachRead<T1>(EntityInAction<T1> action)
		where T1 : struct
	{
		if (action is null) throw new ArgumentNullException(nameof(action));

		_world.ExecuteDirectEntityAction<TQuery, ReadOnlyEntityAction<T1>, T1>(
			_handle,
			new(action),
			trackWrites: false
		);
	}

	/// <summary>
	///     Executes an entity-aware loop over one mutable component.
	/// </summary>
	/// <typeparam name="T1">Mutable component type.</typeparam>
	/// <param name="action">Callback invoked for each matching entity and component.</param>
	/// <exception cref="ArgumentNullException"><paramref name="action" /> is <see langword="null" />.</exception>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">
	///     The query handle belongs to a different world, or fail-fast query-result capacity is exceeded.
	/// </exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain the requested component.</exception>
	/// <remarks>The callback is validated before world lifetime and query-handle ownership.</remarks>
	public void ForEach<T1>(EntityRefAction<T1> action)
		where T1 : struct
	{
		if (action is null) throw new ArgumentNullException(nameof(action));

		_world.ExecuteDirectEntityAction<TQuery, EntityAction<T1>, T1>(
			_handle,
			new(action),
			trackWrites: true
		);
	}

	/// <summary>
	///     Executes an entity-aware loop over one mutable and one read-only component.
	/// </summary>
	/// <typeparam name="T1">Mutable component type.</typeparam>
	/// <typeparam name="T2">Read-only component type.</typeparam>
	/// <param name="action">Callback invoked for each matching entity and components.</param>
	/// <exception cref="ArgumentNullException"><paramref name="action" /> is <see langword="null" />.</exception>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">
	///     The query handle belongs to a different world, or fail-fast query-result capacity is exceeded.
	/// </exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain a requested component.</exception>
	/// <remarks>The callback is validated before world lifetime and query-handle ownership.</remarks>
	public void ForEach<T1, T2>(EntityRefInAction<T1, T2> action)
		where T1 : struct
		where T2 : struct
	{
		if (action is null) throw new ArgumentNullException(nameof(action));

		_world.ExecuteDirectEntityAction<TQuery, EntityAction<T1, T2>, T1, T2>(_handle, new(action));
	}

	/// <summary>
	///     Executes an entity-aware loop over one mutable and two read-only components.
	/// </summary>
	/// <typeparam name="T1">Mutable component type.</typeparam>
	/// <typeparam name="T2">First read-only component type.</typeparam>
	/// <typeparam name="T3">Second read-only component type.</typeparam>
	/// <param name="action">Callback invoked for each matching entity and components.</param>
	/// <exception cref="ArgumentNullException"><paramref name="action" /> is <see langword="null" />.</exception>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">
	///     The query handle belongs to a different world, or fail-fast query-result capacity is exceeded.
	/// </exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain a requested component.</exception>
	/// <remarks>The callback is validated before world lifetime and query-handle ownership.</remarks>
	public void ForEach<T1, T2, T3>(EntityRefInAction<T1, T2, T3> action)
		where T1 : struct
		where T2 : struct
		where T3 : struct
	{
		if (action is null) throw new ArgumentNullException(nameof(action));

		_world.ExecuteDirectEntityAction<TQuery, EntityAction<T1, T2, T3>, T1, T2, T3>(_handle, new(action));
	}

	/// <summary>
	///     Executes an entity-aware loop over one mutable and three read-only components.
	/// </summary>
	/// <typeparam name="T1">Mutable component type.</typeparam>
	/// <typeparam name="T2">First read-only component type.</typeparam>
	/// <typeparam name="T3">Second read-only component type.</typeparam>
	/// <typeparam name="T4">Third read-only component type.</typeparam>
	/// <param name="action">Callback invoked for each matching entity and components.</param>
	/// <exception cref="ArgumentNullException"><paramref name="action" /> is <see langword="null" />.</exception>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">
	///     The query handle belongs to a different world, or fail-fast query-result capacity is exceeded.
	/// </exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain a requested component.</exception>
	/// <remarks>The callback is validated before world lifetime and query-handle ownership.</remarks>
	public void ForEach<T1, T2, T3, T4>(EntityRefInAction<T1, T2, T3, T4> action)
		where T1 : struct
		where T2 : struct
		where T3 : struct
		where T4 : struct
	{
		if (action is null) throw new ArgumentNullException(nameof(action));

		_world.ExecuteDirectEntityAction<TQuery, EntityAction<T1, T2, T3, T4>, T1, T2, T3, T4>(_handle, new(action));
	}

	/// <summary>
	///     Executes a struct job sequentially over the matching entities.
	/// </summary>
	/// <typeparam name="TJob">Job type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The query handle belongs to a different world.</exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain the requested component.</exception>
	public void Run<TJob, T1>(TJob job)
		where TJob : struct, IForEach<T1>
		where T1 : unmanaged =>
		_world.RunDirectFast<TQuery, TJob, T1>(_handle, job);

	/// <summary>
	///     Executes an entity-aware struct job sequentially over the matching entities.
	/// </summary>
	/// <typeparam name="TJob">Entity-aware job type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The query handle belongs to a different world.</exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain the requested component.</exception>
	public void RunEntity<TJob, T1>(TJob job)
		where TJob : struct, IForEachEntity<T1>
		where T1 : unmanaged =>
		_world.RunDirectFastEntity<TQuery, TJob, T1>(_handle, job);

	/// <summary>
	///     Executes a struct job sequentially over the matching entities.
	/// </summary>
	/// <typeparam name="TJob">Job type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">Read-only unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The query handle belongs to a different world.</exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain a requested component.</exception>
	public void Run<TJob, T1, T2>(TJob job)
		where TJob : struct, IForEach<T1, T2>
		where T1 : unmanaged
		where T2 : unmanaged =>
		_world.RunDirectFast<TQuery, TJob, T1, T2>(_handle, job);

	/// <summary>
	///     Executes an entity-aware struct job sequentially over the matching entities.
	/// </summary>
	/// <typeparam name="TJob">Entity-aware job type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">Read-only unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The query handle belongs to a different world.</exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain a requested component.</exception>
	public void RunEntity<TJob, T1, T2>(TJob job)
		where TJob : struct, IForEachEntity<T1, T2>
		where T1 : unmanaged
		where T2 : unmanaged =>
		_world.RunDirectFastEntity<TQuery, TJob, T1, T2>(_handle, job);

	/// <summary>
	///     Executes a struct job sequentially over the matching entities.
	/// </summary>
	/// <typeparam name="TJob">Job type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">First read-only unmanaged component type.</typeparam>
	/// <typeparam name="T3">Second read-only unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The query handle belongs to a different world.</exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain a requested component.</exception>
	public void Run<TJob, T1, T2, T3>(TJob job)
		where TJob : struct, IForEach<T1, T2, T3>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged =>
		_world.RunDirectFast<TQuery, TJob, T1, T2, T3>(_handle, job);

	/// <summary>
	///     Executes an entity-aware struct job sequentially over the matching entities.
	/// </summary>
	/// <typeparam name="TJob">Entity-aware job type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">First read-only unmanaged component type.</typeparam>
	/// <typeparam name="T3">Second read-only unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The query handle belongs to a different world.</exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain a requested component.</exception>
	public void RunEntity<TJob, T1, T2, T3>(TJob job)
		where TJob : struct, IForEachEntity<T1, T2, T3>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged =>
		_world.RunDirectFastEntity<TQuery, TJob, T1, T2, T3>(_handle, job);

	/// <summary>
	///     Executes a struct job sequentially over the matching entities.
	/// </summary>
	/// <typeparam name="TJob">Job type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">First read-only unmanaged component type.</typeparam>
	/// <typeparam name="T3">Second read-only unmanaged component type.</typeparam>
	/// <typeparam name="T4">Third read-only unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The query handle belongs to a different world.</exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain a requested component.</exception>
	public void Run<TJob, T1, T2, T3, T4>(TJob job)
		where TJob : struct, IForEach<T1, T2, T3, T4>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged =>
		_world.RunDirectFast<TQuery, TJob, T1, T2, T3, T4>(_handle, job);

	/// <summary>
	///     Executes an entity-aware struct job sequentially over the matching entities.
	/// </summary>
	/// <typeparam name="TJob">Entity-aware job type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">First read-only unmanaged component type.</typeparam>
	/// <typeparam name="T3">Second read-only unmanaged component type.</typeparam>
	/// <typeparam name="T4">Third read-only unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The query handle belongs to a different world.</exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain a requested component.</exception>
	public void RunEntity<TJob, T1, T2, T3, T4>(TJob job)
		where TJob : struct, IForEachEntity<T1, T2, T3, T4>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged =>
		_world.RunDirectFastEntity<TQuery, TJob, T1, T2, T3, T4>(_handle, job);

	/// <summary>
	///     Executes a struct job in parallel over the matching entities.
	/// </summary>
	/// <typeparam name="TJob">Job type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	/// <param name="degreeOfParallelism">Optional worker limit; the world's configured maximum is used when omitted.</param>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The query handle belongs to a different world.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="degreeOfParallelism" /> is not positive.</exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain the requested component.</exception>
	public void RunParallel<TJob, T1>(TJob job, int? degreeOfParallelism = null)
		where TJob : struct, IForEach<T1>
		where T1 : unmanaged =>
		_world.RunParallelDirect<TQuery, TJob, T1>(_handle, job, degreeOfParallelism);

	/// <summary>
	///     Executes an entity-aware struct job in parallel over the matching entities.
	/// </summary>
	/// <typeparam name="TJob">Entity-aware job type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	/// <param name="degreeOfParallelism">Optional worker limit; the world's configured maximum is used when omitted.</param>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The query handle belongs to a different world.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="degreeOfParallelism" /> is not positive.</exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain the requested component.</exception>
	public void RunParallelEntity<TJob, T1>(TJob job, int? degreeOfParallelism = null)
		where TJob : struct, IForEachEntity<T1>
		where T1 : unmanaged =>
		_world.RunParallelDirectEntity<TQuery, TJob, T1>(_handle, job, degreeOfParallelism);

	/// <summary>
	///     Executes a struct job in parallel over the matching entities.
	/// </summary>
	/// <typeparam name="TJob">Job type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">Read-only unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	/// <param name="degreeOfParallelism">Optional worker limit; the world's configured maximum is used when omitted.</param>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The query handle belongs to a different world.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="degreeOfParallelism" /> is not positive.</exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain a requested component.</exception>
	public void RunParallel<TJob, T1, T2>(TJob job, int? degreeOfParallelism = null)
		where TJob : struct, IForEach<T1, T2>
		where T1 : unmanaged
		where T2 : unmanaged =>
		_world.RunParallelDirect<TQuery, TJob, T1, T2>(_handle, job, degreeOfParallelism);

	/// <summary>
	///     Executes an entity-aware struct job in parallel over the matching entities.
	/// </summary>
	/// <typeparam name="TJob">Entity-aware job type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">Read-only unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	/// <param name="degreeOfParallelism">Optional worker limit; the world's configured maximum is used when omitted.</param>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The query handle belongs to a different world.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="degreeOfParallelism" /> is not positive.</exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain a requested component.</exception>
	public void RunParallelEntity<TJob, T1, T2>(TJob job, int? degreeOfParallelism = null)
		where TJob : struct, IForEachEntity<T1, T2>
		where T1 : unmanaged
		where T2 : unmanaged =>
		_world.RunParallelDirectEntity<TQuery, TJob, T1, T2>(_handle, job, degreeOfParallelism);

	/// <summary>
	///     Executes a struct job in parallel over the matching entities.
	/// </summary>
	/// <typeparam name="TJob">Job type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">First read-only unmanaged component type.</typeparam>
	/// <typeparam name="T3">Second read-only unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	/// <param name="degreeOfParallelism">Optional worker limit; the world's configured maximum is used when omitted.</param>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The query handle belongs to a different world.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="degreeOfParallelism" /> is not positive.</exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain a requested component.</exception>
	public void RunParallel<TJob, T1, T2, T3>(TJob job, int? degreeOfParallelism = null)
		where TJob : struct, IForEach<T1, T2, T3>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged =>
		_world.RunParallelDirect<TQuery, TJob, T1, T2, T3>(_handle, job, degreeOfParallelism);

	/// <summary>
	///     Executes an entity-aware struct job in parallel over the matching entities.
	/// </summary>
	/// <typeparam name="TJob">Entity-aware job type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">First read-only unmanaged component type.</typeparam>
	/// <typeparam name="T3">Second read-only unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	/// <param name="degreeOfParallelism">Optional worker limit; the world's configured maximum is used when omitted.</param>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The query handle belongs to a different world.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="degreeOfParallelism" /> is not positive.</exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain a requested component.</exception>
	public void RunParallelEntity<TJob, T1, T2, T3>(TJob job, int? degreeOfParallelism = null)
		where TJob : struct, IForEachEntity<T1, T2, T3>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged =>
		_world.RunParallelDirectEntity<TQuery, TJob, T1, T2, T3>(_handle, job, degreeOfParallelism);

	/// <summary>
	///     Executes a struct job in parallel over the matching entities.
	/// </summary>
	/// <typeparam name="TJob">Job type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">First read-only unmanaged component type.</typeparam>
	/// <typeparam name="T3">Second read-only unmanaged component type.</typeparam>
	/// <typeparam name="T4">Third read-only unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	/// <param name="degreeOfParallelism">Optional worker limit; the world's configured maximum is used when omitted.</param>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The query handle belongs to a different world.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="degreeOfParallelism" /> is not positive.</exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain a requested component.</exception>
	public void RunParallel<TJob, T1, T2, T3, T4>(TJob job, int? degreeOfParallelism = null)
		where TJob : struct, IForEach<T1, T2, T3, T4>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged =>
		_world.RunParallelDirect<TQuery, TJob, T1, T2, T3, T4>(_handle, job, degreeOfParallelism);

	/// <summary>
	///     Executes an entity-aware struct job in parallel over the matching entities.
	/// </summary>
	/// <typeparam name="TJob">Entity-aware job type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">First read-only unmanaged component type.</typeparam>
	/// <typeparam name="T3">Second read-only unmanaged component type.</typeparam>
	/// <typeparam name="T4">Third read-only unmanaged component type.</typeparam>
	/// <param name="job">Job instance.</param>
	/// <param name="degreeOfParallelism">Optional worker limit; the world's configured maximum is used when omitted.</param>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	/// <exception cref="InvalidOperationException">The query handle belongs to a different world.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="degreeOfParallelism" /> is not positive.</exception>
	/// <exception cref="KeyNotFoundException">A matching archetype does not contain a requested component.</exception>
	public void RunParallelEntity<TJob, T1, T2, T3, T4>(TJob job, int? degreeOfParallelism = null)
		where TJob : struct, IForEachEntity<T1, T2, T3, T4>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged =>
		_world.RunParallelDirectEntity<TQuery, TJob, T1, T2, T3, T4>(_handle, job, degreeOfParallelism);
}

using System.Runtime.CompilerServices;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Internal;
using Bezoro.ECS.Internal.Fixed;
using Bezoro.ECS.Options;
using Bezoro.ECS.Types;
using EntityLocation = Bezoro.ECS.Internal.Fixed.EntityLocation;
using QueryExecutionLease = Bezoro.ECS.Internal.QueryExecutionLease;

namespace Bezoro.ECS.Services;

/// <summary>
///     Owns an isolated ECS simulation context and exposes its entity, component, resource, query, and system APIs.
/// </summary>
/// <remarks>
///     The world is the stable public façade over specialized stores and runtime services. Those collaborators own
///     storage and execution details while this type enforces world lifetime and coordinates cross-service operations.
/// </remarks>
public class World : IWorld, IDisposable
{
	private static          long               _nextInstanceId;
	private readonly        long               _instanceId;
	private readonly        WorldChangeTracker _changeTracker;

	private readonly WorldConfig                 _config;
	private readonly WorldDirectIterationService _directIterationService;
	private readonly WorldEntityStore            _entityStore;
	private readonly WorldLifecycleService       _lifecycleService;
	private readonly WorldQueryEngine            _queryEngine;
	private readonly WorldQueryRuntime           _queryRuntime;
	private readonly WorldRelationIndex          _relationIndex = new();
	private readonly WorldResourceStore          _resourceStore = new();
	private readonly WorldSnapshotCoordinator    _snapshotCoordinator;
	private readonly WorldSnapshotService        _snapshotService;
	private readonly WorldSystemRuntime          _systemRuntime;

	private bool _disposed;
	private bool _disposing;
	private bool _hasConcurrentSystemBatch;

	/// <summary>
	///     Initializes a world with default capacity and scheduling settings.
	/// </summary>
	public World() : this(new WorldConfig()) { }


	/// <summary>
	///     Initializes a world from an explicit configuration.
	/// </summary>
	/// <param name="config">Configuration used by the world's stores and runtimes.</param>
	public World(WorldConfig config)
	{
		_instanceId = Interlocked.Increment(ref _nextInstanceId);
		_config     = config ?? throw new ArgumentNullException(nameof(config));
		_config.Validate();
		_systemRuntime          = new(_config.MaxDegreeOfParallelism);
		_entityStore            = new(this, _config, _relationIndex);
		_changeTracker          = new(_entityStore, _config);
		_queryRuntime           = new(_entityStore, _relationIndex, _changeTracker);
		_directIterationService = new(this);
		_queryEngine            = new(this, _config);
		_snapshotService = new(
			this,
			_config,
			_entityStore,
			_resourceStore,
			_relationIndex,
			_entityStore.TypeById,
			_entityStore.TypeToId
		);

		_lifecycleService    = new(_changeTracker, _entityStore, _queryEngine, _resourceStore, _systemRuntime);
		_snapshotCoordinator = new(_entityStore, _lifecycleService, _queryEngine, _snapshotService);
	}

	/// <summary>
	///     Gets the number of currently alive entities.
	/// </summary>
	public int EntityCount
	{
		get
		{
			ThrowIfDisposed();
			return AliveCount;
		}
	}

	internal int ArchetypeVersionForQueryEngine => ArchetypeVersion;

	internal int EntityCapacity                        => _config.EntityCapacity;
	internal int QueryResultCapacityForDirectIteration => _config.QueryResultCapacity;
	internal int SchedulerPlanBuildCount               => _systemRuntime.PlanBuildCount;

	private bool[] AliveByEntityId => _entityStore.AliveByEntityId;

	private EntityLocation[] LocationByEntityId => _entityStore.LocationByEntityId;

	private int[] VersionByEntityId => _entityStore.VersionByEntityId;

	private List<ArchetypeStorage> Archetypes => _entityStore.Archetypes;

	private int AliveCount
	{
		get => _entityStore.AliveCount;
		set => _entityStore.AliveCount = value;
	}

	private int ArchetypeVersion
	{
		get => _entityStore.ArchetypeVersion;
		set => _entityStore.ArchetypeVersion = value;
	}

	private int ComponentTypeOverflowCount
	{
		get => _entityStore.ComponentTypeOverflowCount;
		set => _entityStore.ComponentTypeOverflowCount = value;
	}

	private int EntityHighWatermark
	{
		get => _entityStore.EntityHighWatermark;
		set => _entityStore.EntityHighWatermark = value;
	}

	private int EntityOverflowCount
	{
		get => _entityStore.EntityOverflowCount;
		set => _entityStore.EntityOverflowCount = value;
	}

	private int RegisteredTypeHighWatermark
	{
		get => _entityStore.RegisteredTypeHighWatermark;
		set => _entityStore.RegisteredTypeHighWatermark = value;
	}

	private int TypeCount
	{
		get => _entityStore.TypeCount;
		set => _entityStore.TypeCount = value;
	}

	/// <inheritdoc />
	public bool Has<T>(Entity entity) where T : struct
	{
		ThrowIfDisposed();
		if (!IsAliveUnchecked(entity))
			return false;

		if (!TryGetRegisteredComponentTypeId<T>(out int typeId))
			return false;
		var location = LocationByEntityId[entity.Id];
		return location.IsValid && Archetypes[location.ArchetypeId].HasType(typeId);
	}

	/// <inheritdoc />
	public bool HasRelation<TRelation>(Entity source, Entity target)
		where TRelation : struct
	{
		ThrowIfDisposed();
		if (!IsAliveUnchecked(source))
			return false;

		if (target == Entity.None)
			return false;

		var sourceLocation = LocationByEntityId[source.Id];
		if (!sourceLocation.IsValid)
			return false;

		var sourceArchetype = Archetypes[sourceLocation.ArchetypeId];
		if (target == Entity.Wildcard)
		{
			int[] relationTypeIds = _relationIndex.GetRelationTypeIds(typeof(TRelation));
			for (var i = 0; i < relationTypeIds.Length; i++)
			{
				if (sourceArchetype.HasType(relationTypeIds[i]))
					return true;
			}

			return false;
		}

		if (!IsAliveUnchecked(target))
			return false;

		return _relationIndex.TryGetRelationTypeId(typeof(TRelation), target, out int relationTypeId) &&
			   sourceArchetype.HasType(relationTypeId);
	}

	/// <inheritdoc />
	public bool HasResource<T>() where T : notnull
	{
		ThrowIfDisposed();
		return _resourceStore.Has<T>();
	}

	/// <inheritdoc />
	public bool IsAlive(Entity entity)
	{
		ThrowIfDisposed();
		return IsAliveUnchecked(entity);
	}

	/// <summary>
	///     Gets whether the system set identified by <typeparamref name="TSet" /> is enabled.
	/// </summary>
	/// <typeparam name="TSet">Type identifying the system set.</typeparam>
	/// <returns><c>true</c> when the set is enabled; otherwise <c>false</c>.</returns>
	public bool IsSystemSetEnabled<TSet>()
	{
		ThrowIfDisposed();
		return _systemRuntime.IsSystemSetEnabled(typeof(TSet));
	}

	/// <inheritdoc />
	public bool RemoveRelation<TRelation>(Entity source, Entity target)
		where TRelation : struct
	{
		ThrowIfDisposed();
		if (!IsAliveUnchecked(source))
			return false;

		var sourceLocation = LocationByEntityId[source.Id];
		if (!sourceLocation.IsValid)
			return false;

		var sourceArchetype = Archetypes[sourceLocation.ArchetypeId];
		if (target == Entity.Wildcard)
		{
			var   removedAny      = false;
			int[] relationTypeIds = _relationIndex.GetRelationTypeIds(typeof(TRelation));
			for (var i = 0; i < relationTypeIds.Length; i++)
			{
				int relationTypeId = relationTypeIds[i];
				sourceLocation = LocationByEntityId[source.Id];
				if (!sourceLocation.IsValid)
					return removedAny;

				sourceArchetype = Archetypes[sourceLocation.ArchetypeId];
				if (!sourceArchetype.HasType(relationTypeId))
					continue;

				RemoveComponentFromCommand(source, relationTypeId);
				removedAny = true;
			}

			return removedAny;
		}

		if (target == Entity.None)
			return false;

		if (!_relationIndex.TryGetRelationTypeId(typeof(TRelation), target, out int relationId))
			return false;

		if (!sourceArchetype.HasType(relationId))
			return false;

		RemoveComponentFromCommand(source, relationId);
		return true;
	}

	/// <inheritdoc />
	public bool RemoveResource<T>() where T : notnull
	{
		ThrowIfDisposed();
		return _resourceStore.Remove<T>();
	}

	/// <inheritdoc />
	public bool TryGet<T>(Entity entity, out T component) where T : struct =>
		TryRead(entity, out component);

	/// <inheritdoc />
	public bool TryRead<T>(Entity entity, out T component) where T : struct
	{
		ThrowIfDisposed();
		component = default;
		if (!IsAliveUnchecked(entity))
			return false;

		if (!TryGetRegisteredComponentTypeId<T>(out int typeId))
			return false;
		return _entityStore.TryGetComponentUnchecked(entity.Id, typeId, out component);
	}

	/// <inheritdoc />
	public bool TryReadResource<T>(out T resource) where T : notnull
	{
		ThrowIfDisposed();
		return _resourceStore.TryRead(out resource);
	}

	/// <inheritdoc />
	public bool TryWrite<T>(Entity entity, out ComponentRef<T> component) where T : struct
	{
		ThrowIfDisposed();
		component = default;
		if (!IsAliveUnchecked(entity))
			return false;

		if (!TryGetRegisteredComponentTypeId<T>(out int typeId))
			return false;
		var location = LocationByEntityId[entity.Id];
		if (!location.IsValid)
			return false;

		var archetype   = Archetypes[location.ArchetypeId];
		int columnIndex = archetype.GetColumnIndexOrNegative(typeId);
		if (columnIndex < 0)
			return false;

		ref var value = ref _entityStore.GetComponentRefUnchecked<T>(entity.Id, typeId);
		TrackPotentialSingleRefWrite(entity.Id, typeId);
		component = new(ref value);
		return true;
	}


	/// <summary>
	///     Creates a fixed-capacity deferred command stream using this world's configuration.
	/// </summary>
	/// <returns>A command stream owned by this world.</returns>
	/// <exception cref="ObjectDisposedException">The world has been disposed.</exception>
	public CommandStream CreateCommandStream()
	{
		ThrowIfDisposed();
		return new(
			this,
			_config.CommandCapacity,
			_config.ComponentTypeCapacity,
			_config.CommandPayloadCapacityPerType,
			_config.OverflowPolicy
		);
	}

	/// <summary>
	///     Creates a reusable accessor for an unmanaged component type.
	/// </summary>
	/// <typeparam name="T">Unmanaged component type.</typeparam>
	/// <returns>An accessor bound to this world.</returns>
	public ComponentAccessor<T> GetAccessor<T>() where T : unmanaged
	{
		ThrowIfDisposed();
		int typeId = GetOrCreateComponentTypeId<T>();
		return new(this, typeId);
	}

	/// <inheritdoc />
	public Entity Spawn()
	{
		ThrowIfDisposed();
		return CreateEntityInternal();
	}

	/// <inheritdoc />
	public Entity Spawn<T1>(in T1 component1) where T1 : struct
	{
		ThrowIfDisposed();
		EnsureStructuralChangesAllowed();
		_ = GetOrCreateComponentTypeId<T1>();
		var entity = CreateEntityInternal();
		ApplySetFromCommand(entity, in component1);
		return entity;
	}

	/// <inheritdoc />
	public Entity Spawn<T1, T2>(in T1 component1, in T2 component2)
		where T1 : struct
		where T2 : struct
	{
		ThrowIfDisposed();
		EnsureStructuralChangesAllowed();
		_ = GetOrCreateComponentTypeId<T1>();
		_ = GetOrCreateComponentTypeId<T2>();
		var entity = CreateEntityInternal();
		ApplySetFromCommand(entity, in component1);
		ApplySetFromCommand(entity, in component2);
		return entity;
	}

	/// <inheritdoc />
	public Entity Spawn<T1, T2, T3>(in T1 component1, in T2 component2, in T3 component3)
		where T1 : struct
		where T2 : struct
		where T3 : struct
	{
		ThrowIfDisposed();
		EnsureStructuralChangesAllowed();
		_ = GetOrCreateComponentTypeId<T1>();
		_ = GetOrCreateComponentTypeId<T2>();
		_ = GetOrCreateComponentTypeId<T3>();
		var entity = CreateEntityInternal();
		ApplySetFromCommand(entity, in component1);
		ApplySetFromCommand(entity, in component2);
		ApplySetFromCommand(entity, in component3);
		return entity;
	}

	/// <inheritdoc />
	public Entity Spawn<T1, T2, T3, T4>(in T1 component1, in T2 component2, in T3 component3, in T4 component4)
		where T1 : struct
		where T2 : struct
		where T3 : struct
		where T4 : struct
	{
		ThrowIfDisposed();
		EnsureStructuralChangesAllowed();
		_ = GetOrCreateComponentTypeId<T1>();
		_ = GetOrCreateComponentTypeId<T2>();
		_ = GetOrCreateComponentTypeId<T3>();
		_ = GetOrCreateComponentTypeId<T4>();
		var entity = CreateEntityInternal();
		ApplySetFromCommand(entity, in component1);
		ApplySetFromCommand(entity, in component2);
		ApplySetFromCommand(entity, in component3);
		ApplySetFromCommand(entity, in component4);
		return entity;
	}

	/// <summary>
	///     Executes a compiled query and returns a cursor over all matching entities.
	/// </summary>
	/// <typeparam name="TSpec">Query specification type.</typeparam>
	/// <param name="handle">Compiled query handle produced by <see cref="Compile{TSpec}" />.</param>
	/// <returns>A <see cref="QueryCursor" /> that must be disposed after use.</returns>
	/// <exception cref="InvalidOperationException">Thrown when <paramref name="handle" /> belongs to a different world.</exception>
	/// <remarks>
	///     While the returned cursor is active, command playback, <see cref="Reset" />, <see cref="Clear" />, and
	///     snapshot capture/restore are rejected.
	/// </remarks>
	public QueryCursor Execute<TSpec>(QueryHandle<TSpec> handle) where TSpec : struct, ICompiledQuerySpec
	{
		ThrowIfDisposed();
		return _queryEngine.Execute(handle);
	}

	/// <summary>
	///     Returns diagnostics for one compiled query handle.
	/// </summary>
	/// <typeparam name="TSpec">Query specification type.</typeparam>
	/// <param name="handle">Compiled query handle.</param>
	/// <returns>Query diagnostics snapshot.</returns>
	public QueryDiagnostics GetQueryDiagnostics<TSpec>(QueryHandle<TSpec> handle)
		where TSpec : struct, ICompiledQuerySpec
	{
		ThrowIfDisposed();
		ThrowIfDirectIterationUnavailable(handle);
		return _queryEngine.GetDiagnostics(handle.Plan);
	}

	/// <summary>
	///     Compiles or retrieves the cached query plan for a query specification.
	/// </summary>
	/// <typeparam name="TSpec">Query specification type.</typeparam>
	/// <returns>A query handle bound to this world.</returns>
	public QueryHandle<TSpec> Compile<TSpec>() where TSpec : struct, ICompiledQuerySpec
	{
		ThrowIfDisposed();
		return _queryEngine.Compile<TSpec>();
	}

	/// <inheritdoc />
	public QueryView<TSpec> Query<TSpec>() where TSpec : struct, ICompiledQuerySpec =>
		new(this, Compile<TSpec>());

	/// <summary>
	///     Gets diagnostics for the current system schedule.
	/// </summary>
	/// <returns>A snapshot of schedule planning and execution state.</returns>
	public ScheduleDiagnostics GetScheduleDiagnostics()
	{
		ThrowIfDisposed();
		return _systemRuntime.GetDiagnostics();
	}

	/// <inheritdoc />
	public ref T GetOrCreateResource<T>() where T : notnull, new()
	{
		ThrowIfDisposed();
		return ref _resourceStore.GetOrCreate<T>();
	}

	/// <summary>
	///     Gets a resource or creates and registers it with the supplied factory.
	/// </summary>
	/// <typeparam name="T">Resource type.</typeparam>
	/// <param name="factory">Factory invoked only when the resource is absent.</param>
	/// <returns>A mutable reference to the registered resource.</returns>
	public ref T GetOrCreateResource<T>(Func<T> factory) where T : notnull
	{
		ThrowIfDisposed();
		return ref _resourceStore.GetOrCreate(factory);
	}

	/// <inheritdoc />
	public ref readonly T Read<T>(Entity entity) where T : struct
	{
		ThrowIfDisposed();
		EnsureAlive(entity);
		if (!TryGetRegisteredComponentTypeId<T>(out int typeId))
			throw new KeyNotFoundException($"Component '{typeof(T).Name}' was not found for entity '{entity.Id}:{entity.Version}'.");
		return ref _entityStore.GetComponentRefUnchecked<T>(entity.Id, typeId);
	}

	/// <inheritdoc />
	public ref readonly T ReadResource<T>() where T : notnull
	{
		ThrowIfDisposed();
		return ref _resourceStore.Get<T>();
	}

	/// <inheritdoc />
	public ref T Write<T>(Entity entity) where T : struct
	{
		ThrowIfDisposed();
		EnsureAlive(entity);
		if (!TryGetRegisteredComponentTypeId<T>(out int typeId))
			throw new KeyNotFoundException($"Component '{typeof(T).Name}' was not found for entity '{entity.Id}:{entity.Version}'.");
		ref var component = ref _entityStore.GetComponentRefUnchecked<T>(entity.Id, typeId);
		TrackPotentialSingleRefWrite(entity.Id, typeId);
		return ref component;
	}

	/// <inheritdoc />
	public ref T WriteResource<T>() where T : notnull
	{
		ThrowIfDisposed();
		return ref _resourceStore.Get<T>();
	}

	/// <inheritdoc />
	public void Add<T>(Entity entity) where T : struct
	{
		ThrowIfDisposed();
		var component = default(T);
		ApplySetFromCommand(entity, in component);
	}

	/// <inheritdoc />
	public void Add<T>(Entity entity, in T component) where T : struct
	{
		ThrowIfDisposed();
		ApplySetFromCommand(entity, in component);
	}

	/// <inheritdoc />
	public void AddRelation<TRelation>(Entity source, Entity target)
		where TRelation : struct
	{
		ThrowIfDisposed();
		EnsureAlive(source);
		if (target == Entity.Wildcard)
			throw new ArgumentException("Cannot add a relation with Entity.Wildcard as the target.", nameof(target));

		EnsureAlive(target);
		EnsureStructuralChangesAllowed();
		int relationTypeId = _relationIndex.GetOrCreateRelationTypeId(
			typeof(TRelation),
			target,
			_entityStore.CreateRelationTypeId
		);

		_entityStore.SetRelationComponent(source, relationTypeId);
	}

	/// <summary>
	///     Registers a system in an execution stage.
	/// </summary>
	/// <param name="system">System instance to register.</param>
	/// <param name="stage">Stage in which the system should execute.</param>
	public void AddSystem(ISystem system, Stage? stage = null)
	{
		ThrowIfDisposed();
		EnsureNoConcurrentSystemBatch("System registration");
		if (_disposing) throw new InvalidOperationException("Systems cannot be registered while the world is disposing.");
		_systemRuntime.AddSystem(this, system, stage);
	}

	/// <summary>
	///     Constructs and registers a system in an execution stage.
	/// </summary>
	/// <typeparam name="TSystem">System type to construct.</typeparam>
	/// <param name="stage">Stage in which the system should execute.</param>
	public void AddSystem<TSystem>(Stage? stage = null)
		where TSystem : ISystem, new()
	{
		ThrowIfDisposed();
		EnsureNoConcurrentSystemBatch("System registration");
		if (_disposing) throw new InvalidOperationException("Systems cannot be registered while the world is disposing.");
		AddSystem(new TSystem(), stage);
	}

	/// <summary>
	///     Captures a snapshot payload and forwards it to <paramref name="writer" />.
	/// </summary>
	/// <typeparam name="TSnapshotWriter">Writer type receiving the captured payload.</typeparam>
	/// <param name="writer">Snapshot writer receiving the captured payload.</param>
	/// <exception cref="InvalidOperationException">Thrown when a query iteration is active.</exception>
	public void CaptureSnapshot<TSnapshotWriter>(ref TSnapshotWriter writer)
		where TSnapshotWriter : struct, IWorldSnapshotWriter
	{
		ThrowIfDisposed();
		EnsureNoConcurrentSystemBatch("Snapshot capture");
		_snapshotCoordinator.Capture(ref writer);
	}

	/// <summary>
	///     Removes all entities, components, resources, and transient query state while retaining allocated capacity.
	/// </summary>
	/// <exception cref="InvalidOperationException">Thrown when a query iteration is active.</exception>
	public void Clear()
	{
		ThrowIfDisposed();
		EnsureNoResourceCallback();
		EnsureNoConcurrentSystemBatch("Clear");
		if (_queryEngine.HasActiveQueryIterations)
			throw new InvalidOperationException("Clear cannot run while a query iteration is active.");

		_lifecycleService.Clear();
	}

	/// <summary>
	///     Removes the run condition associated with a system set.
	/// </summary>
	/// <typeparam name="TSet">Type identifying the system set.</typeparam>
	public void ClearSystemSetRunCondition<TSet>()
	{
		ThrowIfDisposed();
		EnsureNoConcurrentSystemBatch("System-set configuration");
		_systemRuntime.ClearSystemSetRunCondition(typeof(TSet));
	}

	/// <inheritdoc />
	public void Despawn(Entity entity)
	{
		ThrowIfDisposed();
		DestroyEntityInternal(entity);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed || _disposing)
			return;
		EnsureNoResourceCallback();
		if (_queryEngine.HasActiveQueryIterations || _systemRuntime.IsUpdating || _systemRuntime.IsRegistering)
			throw new InvalidOperationException("Dispose cannot run while a query iteration, system update, or system registration is active.");

		_disposing = true;
		try { _lifecycleService.Dispose(this); }
		finally
		{
			_disposed = true;
			_disposing = false;
		}
	}

	/// <summary>
	///     Executes systems assigned to the fixed-tick loop phase.
	/// </summary>
	/// <param name="deltaTime">Elapsed fixed-step time, in seconds.</param>
	public void FixedTick(float deltaTime) => RunPhase(SystemLoopPhase.FixedTick, deltaTime);

	/// <summary>
	///     Executes a sequential loop over one mutable unmanaged component.
	/// </summary>
	/// <typeparam name="TSpec">Query specification type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <param name="handle">Compiled query handle.</param>
	/// <param name="action">Per-entity callback.</param>
	public void ForEach<TSpec, T1>(QueryHandle<TSpec> handle, QueryCursor.RefAction<T1> action)
		where TSpec : struct, ICompiledQuerySpec
		where T1 : unmanaged
	{
		if (action is null) throw new ArgumentNullException(nameof(action));

		ThrowIfDisposed();
		using var cursor = Execute(handle);
		if (!cursor.MoveNext())
			return;

		cursor.ForEach(action);
	}

	/// <summary>
	///     Executes a sequential loop over one mutable and one read-only unmanaged component.
	/// </summary>
	/// <typeparam name="TSpec">Query specification type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">Read-only unmanaged component type.</typeparam>
	/// <param name="handle">Compiled query handle.</param>
	/// <param name="action">Per-entity callback.</param>
	public void ForEach<TSpec, T1, T2>(QueryHandle<TSpec> handle, QueryCursor.RefInAction<T1, T2> action)
		where TSpec : struct, ICompiledQuerySpec
		where T1 : unmanaged
		where T2 : unmanaged
	{
		if (action is null) throw new ArgumentNullException(nameof(action));

		ThrowIfDisposed();
		using var cursor = Execute(handle);
		if (!cursor.MoveNext())
			return;

		cursor.ForEach(action);
	}

	/// <summary>
	///     Executes a sequential loop over one mutable and two read-only unmanaged components.
	/// </summary>
	/// <typeparam name="TSpec">Query specification type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">First read-only unmanaged component type.</typeparam>
	/// <typeparam name="T3">Second read-only unmanaged component type.</typeparam>
	/// <param name="handle">Compiled query handle.</param>
	/// <param name="action">Per-entity callback.</param>
	public void ForEach<TSpec, T1, T2, T3>(QueryHandle<TSpec> handle, QueryCursor.RefInAction<T1, T2, T3> action)
		where TSpec : struct, ICompiledQuerySpec
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
	{
		if (action is null) throw new ArgumentNullException(nameof(action));

		ThrowIfDisposed();
		using var cursor = Execute(handle);
		if (!cursor.MoveNext())
			return;

		cursor.ForEach(action);
	}

	/// <summary>
	///     Executes a no-allocation sequential loop over one mutable and three read-only unmanaged components.
	/// </summary>
	/// <typeparam name="TSpec">Query specification type.</typeparam>
	/// <typeparam name="T1">Mutable unmanaged component type.</typeparam>
	/// <typeparam name="T2">First read-only unmanaged component type.</typeparam>
	/// <typeparam name="T3">Second read-only unmanaged component type.</typeparam>
	/// <typeparam name="T4">Third read-only unmanaged component type.</typeparam>
	/// <param name="handle">Compiled query handle.</param>
	/// <param name="action">Per-entity callback.</param>
	public void ForEach<TSpec, T1, T2, T3, T4>(
		QueryHandle<TSpec>                      handle,
		QueryCursor.RefInAction<T1, T2, T3, T4> action)
		where TSpec : struct, ICompiledQuerySpec
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged
	{
		if (action is null) throw new ArgumentNullException(nameof(action));

		ThrowIfDisposed();
		using var cursor = Execute(handle);
		if (!cursor.MoveNext())
			return;

		cursor.ForEach(action);
	}

	/// <summary>
	///     Executes systems assigned to the late-tick loop phase.
	/// </summary>
	/// <param name="deltaTime">Elapsed frame time, in seconds.</param>
	public void LateTick(float deltaTime) => RunPhase(SystemLoopPhase.LateTick, deltaTime);

	/// <summary>
	///     Applies all commands recorded in a stream owned by this world.
	/// </summary>
	/// <param name="stream">Command stream to apply.</param>
	/// <exception cref="InvalidOperationException">
	///     Thrown when <paramref name="stream" /> belongs to another world or a query iteration is active.
	/// </exception>
	public void Playback(CommandStream stream)
	{
		ThrowIfDisposed();
		if (stream is null) throw new ArgumentNullException(nameof(stream));
		if (!ReferenceEquals(stream.Owner, this))
			throw new InvalidOperationException("Command stream belongs to a different world.");
		EnsureNoConcurrentSystemBatch("Playback");

		if (_queryEngine.HasActiveQueryIterations)
			throw new InvalidOperationException("Playback cannot run while a query iteration is active.");

		stream.PlaybackInternal();
	}

	/// <summary>
	///     Precompiles and caches a query plan without executing it.
	/// </summary>
	/// <typeparam name="TSpec">Query specification type.</typeparam>
	public void Precompile<TSpec>() where TSpec : struct, ICompiledQuerySpec =>
		_ = Compile<TSpec>();

	/// <inheritdoc />
	public void Remove<T>(Entity entity) where T : struct
	{
		ThrowIfDisposed();
		EnsureAlive(entity);
		if (!TryGetRegisteredComponentTypeId<T>(out int typeId))
			return;
		RemoveComponentFromCommand(entity, typeId);
	}

	/// <inheritdoc />
	public void Replace<T>(Entity entity, in T component) where T : struct =>
		Set(entity, in component);

	/// <inheritdoc />
	public void ReplaceResource<T>(T resource) where T : notnull =>
		SetResource(resource);

	/// <summary>
	///     Removes entities and change tracking while retaining resources, registered types, allocated capacity, and diagnostics.
	/// </summary>
	/// <exception cref="InvalidOperationException">Thrown when a query iteration is active.</exception>
	public void Reset()
	{
		ThrowIfDisposed();
		EnsureNoResourceCallback();
		EnsureNoConcurrentSystemBatch("Reset");
		if (_queryEngine.HasActiveQueryIterations)
			throw new InvalidOperationException("Reset cannot run while a query iteration is active.");

		_lifecycleService.Reset();
	}

	/// <summary>
	///     Restores world state from snapshot payload supplied by <paramref name="reader" />.
	/// </summary>
	/// <typeparam name="TSnapshotReader">Reader type providing the snapshot payload.</typeparam>
	/// <param name="reader">Snapshot reader providing restore payload.</param>
	/// <param name="options">Optional type-allowlist and validation options.</param>
	/// <exception cref="InvalidOperationException">Thrown when a query iteration is active or the snapshot is invalid.</exception>
	public void RestoreSnapshot<TSnapshotReader>(
		ref TSnapshotReader             reader,
		SnapshotDeserializationOptions? options = null)
		where TSnapshotReader : struct, IWorldSnapshotReader
	{
		ThrowIfDisposed();
		EnsureNoResourceCallback();
		EnsureNoConcurrentSystemBatch("Snapshot restore");
		_snapshotCoordinator.Restore(ref reader, options);
	}

	/// <summary>
	///     Executes all systems scheduled for a loop phase.
	/// </summary>
	/// <param name="loopPhase">Loop phase to execute.</param>
	/// <param name="deltaTime">Elapsed phase time, in seconds.</param>
	public void RunPhase(SystemLoopPhase loopPhase, float deltaTime)
	{
		ThrowIfDisposed();
		if (_disposing || _systemRuntime.IsRegistering)
			throw new InvalidOperationException("World updates cannot run during system registration or world disposal.");
		_systemRuntime.RunPhase(this, loopPhase, deltaTime);
	}

	/// <inheritdoc />
	public void Set<T>(Entity entity, in T component) where T : struct
	{
		ThrowIfDisposed();
		ApplySetFromCommand(entity, in component);
	}

	/// <inheritdoc />
	public void SetResource<T>(T resource) where T : notnull
	{
		ThrowIfDisposed();
		_resourceStore.Set(resource);
	}

	/// <summary>
	///     Enables or disables a system set.
	/// </summary>
	/// <typeparam name="TSet">Type identifying the system set.</typeparam>
	/// <param name="enabled"><c>true</c> to enable the set; <c>false</c> to skip it.</param>
	public void SetSystemSetEnabled<TSet>(bool enabled)
	{
		ThrowIfDisposed();
		EnsureNoConcurrentSystemBatch("System-set configuration");
		_systemRuntime.SetSystemSetEnabled(typeof(TSet), enabled);
	}

	/// <summary>
	///     Sets the run condition evaluated for a system set.
	/// </summary>
	/// <typeparam name="TSet">Type identifying the system set.</typeparam>
	/// <param name="runCondition">Condition that controls whether the set executes.</param>
	public void SetSystemSetRunCondition<TSet>(ISystemRunCondition runCondition)
	{
		ThrowIfDisposed();
		if (runCondition is null)
			throw new ArgumentNullException(nameof(runCondition));
		EnsureNoConcurrentSystemBatch("System-set configuration");

		_systemRuntime.SetSystemSetRunCondition(typeof(TSet), runCondition);
	}

	/// <summary>
	///     Executes systems assigned to the main tick loop phase.
	/// </summary>
	/// <param name="deltaTime">Elapsed frame time, in seconds.</param>
	public void Tick(float deltaTime) => RunPhase(SystemLoopPhase.Tick, deltaTime);

	/// <summary>
	///     Gets current capacity, high-watermark, and overflow diagnostics for the world.
	/// </summary>
	/// <returns>A diagnostics snapshot.</returns>
	public WorldDiagnostics GetDiagnostics()
	{
		ThrowIfDisposed();
		return new(
			new("Entities", _config.EntityCapacity, AliveCount, EntityHighWatermark, EntityOverflowCount),
			new(
				"ComponentTypes",
				_config.ComponentTypeCapacity,
				TypeCount,
				RegisteredTypeHighWatermark,
				ComponentTypeOverflowCount
			),
			new(
				"QueryResults", _config.QueryResultCapacity, 0, _changeTracker.QueryHighWatermark,
				_changeTracker.QueryOverflowCount
			)
		);
	}

	internal ArchetypeStorage GetArchetypeForCursor(int archetypeId)
	{
		if (unchecked((uint)archetypeId) >= unchecked((uint)Archetypes.Count))
			throw new ArgumentOutOfRangeException(nameof(archetypeId));

		return Archetypes[archetypeId];
	}

	internal bool ContainsReferencesForEntityStore(Type componentType) => ContainsReferences(componentType);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal bool HasComponentForAccessor(
		Entity  entity,
		int     typeId,
		ref int cachedArchetypeId,
		ref int cachedColumnIndex)
	{
		ThrowIfDisposed();
		return _entityStore.HasComponentForAccessor(
			entity,
			typeId,
			ref cachedArchetypeId,
			ref cachedColumnIndex
		);
	}

	internal bool MatchesRemoveTransitionSource(
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

	internal bool MatchesSetTransitionSource(
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
	internal bool TryGetComponentForAccessor<T>(
		Entity  entity,
		int     typeId,
		ref int cachedArchetypeId,
		ref int cachedColumnIndex,
		out T   component)
		where T : unmanaged
	{
		ThrowIfDisposed();
		return _entityStore.TryGetComponentForAccessor(
			entity,
			typeId,
			ref cachedArchetypeId,
			ref cachedColumnIndex,
			out component
		);
	}

	internal Entity CreateEntityInternal()
	{
		EnsureStructuralChangesAllowed();
		return _entityStore.CreateEntity();
	}

	internal void EnsureStructuralChangesAllowed()
	{
		if (_queryEngine.HasActiveQueryIterations || Volatile.Read(ref _hasConcurrentSystemBatch))
			throw new InvalidOperationException(
				"Structural changes cannot run during a query iteration or concurrent system batch. Record commands and play them back after execution."
			);
	}

	internal void SetConcurrentSystemBatch(bool active) => Volatile.Write(ref _hasConcurrentSystemBatch, active);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal Entity GetEntityForCursor(int entityId) =>
		new(entityId, VersionByEntityId[entityId]);

	internal int FillQueryResultsForQueryEngine(
		CompiledQueryPlan plan,
		QueryChunkMatch[] chunkMatches,
		int               entityCapacity,
		out int           chunkMatchCount,
		bool              advanceObservedChangeVersion = true) =>
		_queryRuntime.FillQueryResults(
			plan,
			chunkMatches,
			entityCapacity,
			out chunkMatchCount,
			advanceObservedChangeVersion
		);

	internal int GetArchetypeEntityCount(int archetypeId)
		=> _entityStore.GetArchetypeEntityCount(archetypeId);

	internal int GetOrCreateComponentTypeId<T>() where T : struct
	{
		if (TryGetRegisteredComponentTypeId<T>(out int registered)) return registered;

		int typeId = _entityStore.GetOrCreateComponentTypeIdGeneric<T>();
		ComponentTypeIdCache<T>.TypeId = typeId;
		ComponentTypeIdCache<T>.OwnerInstanceId = _instanceId;
		return typeId;
	}

	private bool TryGetRegisteredComponentTypeId<T>(out int typeId) where T : struct
	{
		if (ComponentTypeIdCache<T>.OwnerInstanceId == _instanceId)
		{
			typeId = ComponentTypeIdCache<T>.TypeId;
			return true;
		}
		if (!_entityStore.TypeToId.TryGetValue(typeof(T), out typeId)) return false;
		ComponentTypeIdCache<T>.TypeId = typeId;
		ComponentTypeIdCache<T>.OwnerInstanceId = _instanceId;
		return true;
	}

	internal int GetOrCreateComponentTypeId(Type componentType)
	{
		if (componentType is null) throw new ArgumentNullException(nameof(componentType));
		if (!componentType.IsValueType)
			throw new ArgumentException("Components must be value types.", nameof(componentType));

		return _entityStore.GetOrCreateComponentTypeId(componentType, ContainsReferences(componentType));
	}

	internal int GetOrRefreshMatchingArchetypesForQueryEngine(CompiledQueryPlan plan) =>
		_queryRuntime.GetOrRefreshMatchingArchetypes(plan);

	internal int ResolveDegreeOfParallelismForDirectIteration(int? degreeOfParallelism) =>
		ResolveDegreeOfParallelism(degreeOfParallelism);

	internal int[] BuildMaskWordIndices(ulong[] maskWords) => _entityStore.BuildMaskWordIndices(maskWords);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal int[] GetEntityVersionsForCursor() => VersionByEntityId;

	internal ref T GetComponentRefForCursorUnchecked<T>(int entityId, int typeId) where T : struct
		=> ref _entityStore.GetComponentRefForCursorUnchecked<T>(entityId, typeId);

	internal Type[] ResolveTypesForQueryEngine(int[] typeIds) => _queryRuntime.ResolveTypes(typeIds);

	internal ulong AdvanceComponentChangeVersionForEntityStore() => _changeTracker.AdvanceComponentChangeVersion();

	internal ulong[] BuildMaskWords(int[] typeIds) => _entityStore.BuildMaskWords(typeIds);

	internal void AcquireQueryChunkMatchScratchForDirectIteration(
		out QueryChunkMatch[] chunkMatches,
		out bool              usesSharedScratch)
	{
		AcquireQueryChunkMatchScratch(out chunkMatches, out usesSharedScratch);
		_queryEngine.EnterQueryIteration();
	}

	internal void AddRelationFromSnapshot(Type relationType, Entity source, Entity target)
	{
		if (relationType is null) throw new ArgumentNullException(nameof(relationType));
		if (target == Entity.Wildcard || target == Entity.None)
			throw new ArgumentException("Snapshot relation target must be a concrete entity.", nameof(target));

		EnsureAlive(source);
		EnsureAlive(target);
		int relationTypeId = _relationIndex.GetOrCreateRelationTypeId(
			relationType,
			target,
			_entityStore.CreateRelationTypeId
		);

		_entityStore.SetRelationComponent(source, relationTypeId);
	}

	internal void ApplySetBatchFromCommandKnownTransition<T>(
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
		=> _entityStore.ApplySetBatchFromCommandKnownTransition(
			entityIds,
			entityOffset,
			count,
			payloads,
			payloadCount,
			payloadIndices,
			payloadOffset,
			typeId,
			sourceArchetypeId,
			targetArchetypeId
		);

	internal void ApplySetBatchFromCommandKnownTransitionFast<T>(
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
		=> _entityStore.ApplySetBatchFromCommandKnownTransitionFast(
			entityIds,
			entityOffset,
			count,
			payloads,
			payloadIndices,
			payloadOffset,
			typeId,
			sourceArchetypeId,
			targetArchetypeId
		);

	internal void ApplySetFromCommand<T>(Entity entity, in T component) where T : struct
		=> _entityStore.ApplySetFromCommand(entity, component);

	internal void ApplySetFromCommandKnownTransition<T>(
		Entity entity,
		in T   component,
		int    typeId,
		int    sourceArchetypeId,
		int    targetArchetypeId)
		where T : struct
		=> _entityStore.ApplySetFromCommandKnownTransition(
			entity,
			component,
			typeId,
			sourceArchetypeId,
			targetArchetypeId
		);

	internal void ClearEntityComponentVersionsForEntityStore(int entityId, int typeCount) =>
		_changeTracker.ClearEntityComponentVersions(entityId, typeCount);

	internal void DescribeRemoveTransition(
		Entity  entity,
		int     typeId,
		out int sourceArchetypeId,
		out int targetArchetypeId)
		=> _entityStore.DescribeRemoveTransition(entity, typeId, out sourceArchetypeId, out targetArchetypeId);

	internal void DescribeSetTransition(Entity entity, int typeId, out int sourceArchetypeId, out int targetArchetypeId)
		=> _entityStore.DescribeSetTransition(entity, typeId, out sourceArchetypeId, out targetArchetypeId);

	internal void DestroyEntityInternal(Entity entity)
	{
		EnsureStructuralChangesAllowed();
		_entityStore.DestroyEntity(entity);
	}

	internal void EnableRefWriteTrackingForQuery(CompiledQueryPlan plan)
		=> _changeTracker.EnableRefWriteTracking(plan);

	internal void ExecuteDirectEntityAction<TSpec, TAction, T1>(
		QueryHandle<TSpec> handle,
		TAction            action,
		bool               trackWrites)
		where TSpec : struct, ICompiledQuerySpec
		where TAction : struct, IEntityChunkAction<T1>
		where T1 : struct
	{
		ThrowIfDisposed();
		_queryEngine.EnterQueryIteration();
		try
		{
			_directIterationService.ExecuteDirectEntityAction<TSpec, TAction, T1>(
				handle,
				action,
				trackWrites: trackWrites
			);
		}
		finally
		{
			_queryEngine.ExitQueryIteration();
		}
	}

	internal void ExecuteDirectEntityAction<TSpec, TAction, T1, T2>(QueryHandle<TSpec> handle, TAction action)
		where TSpec : struct, ICompiledQuerySpec
		where TAction : struct, IEntityChunkAction<T1, T2>
		where T1 : struct
		where T2 : struct
	{
		ThrowIfDisposed();
		_queryEngine.EnterQueryIteration();
		try
		{
			_directIterationService.ExecuteDirectEntityAction<TSpec, TAction, T1, T2>(handle, action);
		}
		finally
		{
			_queryEngine.ExitQueryIteration();
		}
	}

	internal void ExecuteDirectEntityAction<TSpec, TAction, T1, T2, T3>(QueryHandle<TSpec> handle, TAction action)
		where TSpec : struct, ICompiledQuerySpec
		where TAction : struct, IEntityChunkAction<T1, T2, T3>
		where T1 : struct
		where T2 : struct
		where T3 : struct
	{
		ThrowIfDisposed();
		_queryEngine.EnterQueryIteration();
		try
		{
			_directIterationService.ExecuteDirectEntityAction<TSpec, TAction, T1, T2, T3>(handle, action);
		}
		finally
		{
			_queryEngine.ExitQueryIteration();
		}
	}

	internal void ExecuteDirectEntityAction<TSpec, TAction, T1, T2, T3, T4>(QueryHandle<TSpec> handle, TAction action)
		where TSpec : struct, ICompiledQuerySpec
		where TAction : struct, IEntityChunkAction<T1, T2, T3, T4>
		where T1 : struct
		where T2 : struct
		where T3 : struct
		where T4 : struct
	{
		ThrowIfDisposed();
		_queryEngine.EnterQueryIteration();
		try
		{
			_directIterationService.ExecuteDirectEntityAction<TSpec, TAction, T1, T2, T3, T4>(handle, action);
		}
		finally
		{
			_queryEngine.ExitQueryIteration();
		}
	}

	internal void HandleQueryOverflowForDirectIteration() => _changeTracker.HandleQueryOverflow();

	internal void MarkComponentAddedForEntityStore(int entityId, int typeId, ulong version) =>
		_changeTracker.MarkComponentAdded(entityId, typeId, version);

	internal void MarkComponentChangedForEntityStore(int entityId, int typeId, ulong version) =>
		_changeTracker.MarkComponentChanged(entityId, typeId, version);

	internal void MaterializeQueryEntities(
		QueryChunkMatch[] chunkMatches,
		int               chunkMatchCount,
		Entity[]          destination,
		int               entityCount)
		=> _queryEngine.MaterializeQueryEntities(chunkMatches, chunkMatchCount, destination, entityCount);

	internal void ObserveDirectIterationEntityCount(int processedEntityCount)
		=> _changeTracker.ObserveDirectIterationEntityCount(processedEntityCount);

	internal void ReleaseCursorQueryExecution(
		QueryChunkMatch[]   chunkMatches,
		Entity[]            entities,
		bool                usesSharedScratch,
		QueryExecutionLease lease)
		=> _queryEngine.ReleaseCursorQueryExecution(chunkMatches, entities, usesSharedScratch, lease);

	internal void ReleaseQueryChunkMatchScratchForDirectIteration(
		QueryChunkMatch[] chunkMatches,
		bool              usesSharedScratch)
	{
		try { ReleaseQueryChunkMatchScratch(chunkMatches, usesSharedScratch); }
		finally { _queryEngine.ExitQueryIteration(); }
	}

	internal void RemoveAllComponentsFromCommandKnownTransitionFast(int sourceArchetypeId, int targetArchetypeId)
		=> _entityStore.RemoveAllComponentsFromCommandKnownTransitionFast(sourceArchetypeId, targetArchetypeId);

	internal void RemoveComponentBatchFromCommandKnownTransition(
		int[] entityIds,
		int   entityOffset,
		int   count,
		int   typeId,
		int   sourceArchetypeId,
		int   targetArchetypeId)
		=> _entityStore.RemoveComponentBatchFromCommandKnownTransition(
			entityIds,
			entityOffset,
			count,
			typeId,
			sourceArchetypeId,
			targetArchetypeId
		);

	internal void RemoveComponentBatchFromCommandKnownTransitionFast(
		int[] entityIds,
		int   entityOffset,
		int   count,
		int   sourceArchetypeId,
		int   targetArchetypeId)
		=> _entityStore.RemoveComponentBatchFromCommandKnownTransitionFast(
			entityIds,
			entityOffset,
			count,
			sourceArchetypeId,
			targetArchetypeId
		);

	internal void RemoveComponentFromCommand(Entity entity, int typeId)
		=> _entityStore.RemoveComponentFromCommand(entity, typeId);

	internal void RemoveComponentFromCommandKnownTransition(
		Entity entity,
		int    typeId,
		int    sourceArchetypeId,
		int    targetArchetypeId)
		=> _entityStore.RemoveComponentFromCommandKnownTransition(
			entity,
			typeId,
			sourceArchetypeId,
			targetArchetypeId
		);

	internal void RemoveMarkedComponentsFromCommandKnownTransitionFast(
		uint[] batchEntityMarkerBits,
		int    sourceArchetypeId,
		int    targetArchetypeId)
		=> _entityStore.RemoveMarkedComponentsFromCommandKnownTransitionFast(
			batchEntityMarkerBits,
			sourceArchetypeId,
			targetArchetypeId
		);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal void ResolveAccessorLocation(
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

		ThrowIfDisposed();
		_entityStore.ResolveAccessorLocation(
			entity,
			typeId,
			componentType,
			ref cachedArchetypeId,
			ref cachedColumnIndex,
			out archetype,
			out chunkIndex,
			out rowIndex
		);
	}

	internal void RunDirectFast<TSpec, TJob, T1>(QueryHandle<TSpec> handle, TJob job)
		where TSpec : struct, ICompiledQuerySpec
		where TJob : struct, IForEach<T1>
		where T1 : unmanaged
	{
		ThrowIfDisposed();
		_directIterationService.RunDirectFast<TSpec, TJob, T1>(handle, job);
	}

	internal void RunDirectFast<TSpec, TJob, T1, T2>(QueryHandle<TSpec> handle, TJob job)
		where TSpec : struct, ICompiledQuerySpec
		where TJob : struct, IForEach<T1, T2>
		where T1 : unmanaged
		where T2 : unmanaged
	{
		ThrowIfDisposed();
		_directIterationService.RunDirectFast<TSpec, TJob, T1, T2>(handle, job);
	}

	internal void RunDirectFast<TSpec, TJob, T1, T2, T3>(QueryHandle<TSpec> handle, TJob job)
		where TSpec : struct, ICompiledQuerySpec
		where TJob : struct, IForEach<T1, T2, T3>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
	{
		ThrowIfDisposed();
		_directIterationService.RunDirectFast<TSpec, TJob, T1, T2, T3>(handle, job);
	}

	internal void RunDirectFast<TSpec, TJob, T1, T2, T3, T4>(QueryHandle<TSpec> handle, TJob job)
		where TSpec : struct, ICompiledQuerySpec
		where TJob : struct, IForEach<T1, T2, T3, T4>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged
	{
		ThrowIfDisposed();
		_directIterationService.RunDirectFast<TSpec, TJob, T1, T2, T3, T4>(handle, job);
	}

	internal void RunDirectFastEntity<TSpec, TJob, T1>(QueryHandle<TSpec> handle, TJob job)
		where TSpec : struct, ICompiledQuerySpec
		where TJob : struct, IForEachEntity<T1>
		where T1 : unmanaged
	{
		ThrowIfDisposed();
		_directIterationService.RunDirectFastEntity<TSpec, TJob, T1>(handle, job);
	}

	internal void RunDirectFastEntity<TSpec, TJob, T1, T2>(QueryHandle<TSpec> handle, TJob job)
		where TSpec : struct, ICompiledQuerySpec
		where TJob : struct, IForEachEntity<T1, T2>
		where T1 : unmanaged
		where T2 : unmanaged
	{
		ThrowIfDisposed();
		_directIterationService.RunDirectFastEntity<TSpec, TJob, T1, T2>(handle, job);
	}

	internal void RunDirectFastEntity<TSpec, TJob, T1, T2, T3>(QueryHandle<TSpec> handle, TJob job)
		where TSpec : struct, ICompiledQuerySpec
		where TJob : struct, IForEachEntity<T1, T2, T3>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
	{
		ThrowIfDisposed();
		_directIterationService.RunDirectFastEntity<TSpec, TJob, T1, T2, T3>(handle, job);
	}

	internal void RunDirectFastEntity<TSpec, TJob, T1, T2, T3, T4>(QueryHandle<TSpec> handle, TJob job)
		where TSpec : struct, ICompiledQuerySpec
		where TJob : struct, IForEachEntity<T1, T2, T3, T4>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged
	{
		ThrowIfDisposed();
		_directIterationService.RunDirectFastEntity<TSpec, TJob, T1, T2, T3, T4>(handle, job);
	}

	internal void RunParallelDirect<TSpec, TJob, T1>(QueryHandle<TSpec> handle, TJob job, int? degreeOfParallelism = null)
		where TSpec : struct, ICompiledQuerySpec
		where TJob : struct, IForEach<T1>
		where T1 : unmanaged
	{
		ThrowIfDisposed();
		_directIterationService.RunParallel<TSpec, TJob, T1>(handle, job, degreeOfParallelism);
	}

	internal void RunParallelDirect<TSpec, TJob, T1, T2>(QueryHandle<TSpec> handle, TJob job, int? degreeOfParallelism = null)
		where TSpec : struct, ICompiledQuerySpec
		where TJob : struct, IForEach<T1, T2>
		where T1 : unmanaged
		where T2 : unmanaged
	{
		ThrowIfDisposed();
		_directIterationService.RunParallel<TSpec, TJob, T1, T2>(handle, job, degreeOfParallelism);
	}

	internal void RunParallelDirect<TSpec, TJob, T1, T2, T3>(QueryHandle<TSpec> handle, TJob job, int? degreeOfParallelism = null)
		where TSpec : struct, ICompiledQuerySpec
		where TJob : struct, IForEach<T1, T2, T3>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
	{
		ThrowIfDisposed();
		_directIterationService.RunParallel<TSpec, TJob, T1, T2, T3>(handle, job, degreeOfParallelism);
	}

	internal void RunParallelDirect<TSpec, TJob, T1, T2, T3, T4>(QueryHandle<TSpec> handle, TJob job, int? degreeOfParallelism = null)
		where TSpec : struct, ICompiledQuerySpec
		where TJob : struct, IForEach<T1, T2, T3, T4>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged
	{
		ThrowIfDisposed();
		_directIterationService.RunParallel<TSpec, TJob, T1, T2, T3, T4>(handle, job, degreeOfParallelism);
	}

	internal void RunParallelDirectEntity<TSpec, TJob, T1>(QueryHandle<TSpec> handle, TJob job, int? degreeOfParallelism = null)
		where TSpec : struct, ICompiledQuerySpec
		where TJob : struct, IForEachEntity<T1>
		where T1 : unmanaged
	{
		ThrowIfDisposed();
		_directIterationService.RunParallelEntity<TSpec, TJob, T1>(handle, job, degreeOfParallelism);
	}

	internal void RunParallelDirectEntity<TSpec, TJob, T1, T2>(QueryHandle<TSpec> handle, TJob job, int? degreeOfParallelism = null)
		where TSpec : struct, ICompiledQuerySpec
		where TJob : struct, IForEachEntity<T1, T2>
		where T1 : unmanaged
		where T2 : unmanaged
	{
		ThrowIfDisposed();
		_directIterationService.RunParallelEntity<TSpec, TJob, T1, T2>(handle, job, degreeOfParallelism);
	}

	internal void RunParallelDirectEntity<TSpec, TJob, T1, T2, T3>(QueryHandle<TSpec> handle, TJob job, int? degreeOfParallelism = null)
		where TSpec : struct, ICompiledQuerySpec
		where TJob : struct, IForEachEntity<T1, T2, T3>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
	{
		ThrowIfDisposed();
		_directIterationService.RunParallelEntity<TSpec, TJob, T1, T2, T3>(handle, job, degreeOfParallelism);
	}

	internal void RunParallelDirectEntity<TSpec, TJob, T1, T2, T3, T4>(QueryHandle<TSpec> handle, TJob job, int? degreeOfParallelism = null)
		where TSpec : struct, ICompiledQuerySpec
		where TJob : struct, IForEachEntity<T1, T2, T3, T4>
		where T1 : unmanaged
		where T2 : unmanaged
		where T3 : unmanaged
		where T4 : unmanaged
	{
		ThrowIfDisposed();
		_directIterationService.RunParallelEntity<TSpec, TJob, T1, T2, T3, T4>(handle, job, degreeOfParallelism);
	}

	internal void SetComponentFromSnapshot(Entity entity, Type componentType, object value)
		=> _entityStore.SetComponentBoxed(entity, componentType, value);

	internal void SetResourceBoxedFromSnapshot(Type resourceType, object value)
	{
		_resourceStore.SetBoxed(resourceType, value);
	}

	internal void TrackPotentialAccessorRefWrite(
		ArchetypeStorage.Chunk chunk,
		int                    columnIndex,
		int                    typeId,
		int                    rowIndex)
		=> _changeTracker.TrackPotentialAccessorRefWrite(chunk, columnIndex, typeId, rowIndex);

	internal void TrackPotentialChunkMatchRefWrites(
		QueryChunkMatch[] chunkMatches,
		int               chunkMatchCount,
		int               typeId)
		=> _changeTracker.TrackPotentialChunkMatchRefWrites(chunkMatches, chunkMatchCount, typeId);

	internal void TrackPotentialCursorRefWrite(
		ArchetypeStorage.Chunk chunk,
		int                    columnIndex,
		int                    typeId,
		int                    rowIndex) =>
		_changeTracker.TrackPotentialCursorRefWrite(chunk, columnIndex, typeId, rowIndex);

	internal void TrackPotentialDirectFastRefWritesForDirectIteration(
		int[] matchingArchetypeIds,
		int   matchCount,
		int   typeId) =>
		TrackPotentialDirectFastRefWrites(matchingArchetypeIds, matchCount, typeId);

	internal void ValidateDirectIterationHandle<TSpec>(QueryHandle<TSpec> handle)
		where TSpec : struct, ICompiledQuerySpec
	{
		if (handle.Plan is null)
			throw new InvalidOperationException("Query handle is uninitialized. Compile the query before executing it.");
		if (!ReferenceEquals(handle.Plan.Owner, this))
			throw new InvalidOperationException("Query handle belongs to a different world.");
	}

	private static bool ContainsReferences(Type type) => ComponentTypeTraits.ContainsReferences(type);



	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool IsAliveUnchecked(Entity entity) =>
		entity.Id >= 0 &&
		entity.Id < AliveByEntityId.Length &&
		AliveByEntityId[entity.Id] &&
		VersionByEntityId[entity.Id] == entity.Version;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private int ResolveDegreeOfParallelism(int? degreeOfParallelism)
	{
		if (degreeOfParallelism is <= 0)
			throw new ArgumentOutOfRangeException(
				nameof(degreeOfParallelism),
				"Degree of parallelism must be greater than zero."
			);

		return degreeOfParallelism ?? _config.MaxDegreeOfParallelism;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private ulong AdvanceComponentChangeVersion()
		=> _changeTracker.AdvanceComponentChangeVersion();

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void AcquireQueryChunkMatchScratch(out QueryChunkMatch[] chunkMatches, out bool usesSharedScratch) =>
		_queryEngine.AcquireQueryChunkMatchScratch(out chunkMatches, out usesSharedScratch);

	private void EnsureAlive(Entity entity)
	{
		if (!IsAliveUnchecked(entity))
			throw new InvalidOperationException($"Entity '{entity.Id}:{entity.Version}' is not alive.");
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void MarkComponentAdded(int entityId, int typeId, ulong version) =>
		_changeTracker.MarkComponentAdded(entityId, typeId, version);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void MarkComponentChanged(int entityId, int typeId, ulong version) =>
		_changeTracker.MarkComponentChanged(entityId, typeId, version);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ReleaseQueryChunkMatchScratch(QueryChunkMatch[] chunkMatches, bool usesSharedScratch) =>
		_queryEngine.ReleaseQueryChunkMatchScratch(chunkMatches, usesSharedScratch);

	private void ThrowIfDirectIterationUnavailable<TSpec>(QueryHandle<TSpec> handle)
		where TSpec : struct, ICompiledQuerySpec =>
		ValidateDirectIterationHandle(handle);

	private void ThrowIfDisposed()
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(World));
	}

	private void EnsureNoResourceCallback()
	{
		if (_resourceStore.IsInvokingUserCode)
			throw new InvalidOperationException("World lifetime operations cannot run from a resource factory or disposal callback.");
	}

	private void EnsureNoConcurrentSystemBatch(string operation)
	{
		if (Volatile.Read(ref _hasConcurrentSystemBatch))
			throw new InvalidOperationException($"{operation} cannot run during a concurrent system batch.");
	}

	private void TrackPotentialDirectFastRefWrites(int[] matchingArchetypeIds, int matchCount, int typeId)
		=> _changeTracker.TrackPotentialDirectFastRefWrites(matchingArchetypeIds, matchCount, typeId);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void TrackPotentialQueryChunkMatchRefWrites(int chunkMatchCount, int typeId)
		=> TrackPotentialChunkMatchRefWrites(_queryEngine.SharedChunkMatches, chunkMatchCount, typeId);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void TrackPotentialQueryChunkMatchRefWrites(
		QueryChunkMatch[] chunkMatches,
		int               chunkMatchCount,
		int               typeId)
		=> TrackPotentialChunkMatchRefWrites(chunkMatches, chunkMatchCount, typeId);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void TrackPotentialSingleRefWrite(int entityId, int typeId)
	{
		var location = LocationByEntityId[entityId];
		if (!location.IsValid)
			return;

		var archetype   = Archetypes[location.ArchetypeId];
		int columnIndex = archetype.GetColumnIndexOrNegative(typeId);
		if (columnIndex < 0)
			return;

		var chunk = archetype.GetChunkUnchecked(location.ChunkIndex);
		_changeTracker.TrackPotentialAccessorRefWrite(chunk, columnIndex, typeId, location.RowIndex);
	}

	private static class ComponentTypeIdCache<T> where T : struct
	{
		// A shared owner/ID pair can race when independent worlds run on different threads.
		[ThreadStatic] public static long OwnerInstanceId;
		[ThreadStatic] public static int TypeId;
	}
}

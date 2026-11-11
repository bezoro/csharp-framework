using System.Buffers;
using Bezoro.ECS.Internal.Fixed;
using Bezoro.ECS.Services;

namespace Bezoro.ECS.Types;

/// <summary>
///     Fixed-capacity deferred command stream for structural mutations in <see cref="World" />.
///     Command storage is allocated on the first recorded command and retained until disposal.
/// </summary>
/// <remarks>
///     Recording, diagnostics, reset, disposal and playback serialize per stream. Concurrent producers retain
///     each caller's order; order between callers depends on scheduling. World playback still requires exclusive
///     access to the owning world's structural state. Stop producers before playback for a fixed batch boundary.
/// </remarks>
public sealed class CommandStream : IDisposable
{
	private static long _nextTemporaryGeneration;
	private readonly object _gate = new();
	private readonly int                      _commandCapacity;
	private readonly int                      _componentTypeCapacity;
	private readonly int                      _payloadCapacityPerType;
	private          RecordedCommand[]?       _commands;
	private readonly WorldOverflowPolicy      _overflowPolicy;
	private          bool                     _disposed;
	private          bool                     _isPlayingBack;
	private          Entity[]?                _resolvedTemporaryEntities;
	private          ICommandPayloadStore?[]? _payloadStoresByTypeId;
	private          int                      _batchTouchedMarkerWordCount;
	private          int                      _commandCount;
	private          int                      _highWatermark;
	private          int                      _nextTemporaryId = -3;
	private          int                      _overflowCount;
	private          int                      _payloadStoreTypeCount;
	private          int                      _temporaryResolveGeneration;
	private          int[]?                   _batchEntityIds;
	private          int[]?                   _batchPayloadIndices;
	private          int[]?                   _batchTouchedMarkerWordIndices;
	private          int[]?                   _payloadStoreTypeIds;
	private          int[]?                   _resolvedTemporaryGenerationByIndex;
	private          uint[]?                  _batchEntityMarkerBits;

	internal CommandStream(
		World               world,
		int                 commandCapacity,
		int                 componentTypeCapacity,
		int                 payloadCapacityPerType,
		WorldOverflowPolicy overflowPolicy)
	{
		Owner                   = world;
		_commandCapacity        = commandCapacity;
		_componentTypeCapacity  = componentTypeCapacity;
		_payloadCapacityPerType = payloadCapacityPerType;
		_overflowPolicy         = overflowPolicy;
	}

	/// <summary>
	///     Indicates whether commands are currently recorded.
	/// </summary>
	public bool HasCommands
	{
		get
		{
			lock (_gate)
			{
				ThrowIfDisposed();
				return _commandCount > 0;
			}
		}
	}

	internal World Owner { get; }

	/// <summary>
	///     Returns command-buffer diagnostics for this stream.
	/// </summary>
	public CommandStreamDiagnostics GetDiagnostics()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return new(_commandCapacity, _commandCount, _highWatermark, _overflowCount);
		}
	}

	/// <summary>
	///     Records a deferred entity creation command.
	/// </summary>
	/// <returns>A temporary entity handle resolved during playback.</returns>
	public Entity CreateEntity()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!CanRecordCommand())
				throw new InvalidOperationException("Command stream is full and cannot allocate another temporary entity.");
			EnsureTemporaryGeneration();

			if (!TryRecord(new(RecordedCommandType.CreateEntity, new(_nextTemporaryId--, _temporaryResolveGeneration), -1, -1), out var command))
				throw new InvalidOperationException("Command stream is full and cannot allocate another temporary entity.");

			return command.Entity;
		}
	}

	/// <summary>
	///     Records a deferred entity creation command with one initial component value.
	/// </summary>
	/// <typeparam name="T">Component type.</typeparam>
	/// <param name="component">Initial component value.</param>
	/// <returns>A temporary entity handle resolved during playback.</returns>
	public Entity CreateEntity<T>(in T component) where T : struct
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!CanRecordCommand())
				throw new InvalidOperationException("Command stream is full and cannot allocate another temporary entity.");
			EnsureTemporaryGeneration();

			int typeId = Owner.GetOrCreateComponentTypeId<T>();
			var store  = GetOrCreatePayloadStore<T>(typeId);
			if (store.IsFull) _overflowCount++;
			if (!store.TryAdd(in component, out int payloadIndex))
			{
				throw new InvalidOperationException(
					$"Payload capacity for component '{typeof(T).Name}' was exceeded."
				);
			}

			if (!TryRecord(
					new(
						RecordedCommandType.CreateEntityWithComponent,
						new(_nextTemporaryId--, _temporaryResolveGeneration),
						typeId,
						payloadIndex
					),
					out var command
				))
				throw new InvalidOperationException("Command stream is full and cannot allocate another temporary entity.");

			return command.Entity;
		}
	}

	/// <summary>
	///     Records a deferred destroy command.
	/// </summary>
	/// <remarks>
	///     If the stream is full the command is silently dropped and the overflow counter is incremented.
	///     Use <see cref="GetDiagnostics" /> to detect lost commands.
	/// </remarks>
	/// <param name="entity">Entity to destroy.</param>
	public void Destroy(Entity entity)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!CanRecordCommand())
				return;

			TryRecord(new(RecordedCommandType.DestroyEntity, entity, -1, -1), out _);
		}
	}

	/// <inheritdoc />
	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
				return;

			ResetInternal();
			var    payloadStoresByTypeId = _payloadStoresByTypeId;
			int[]? payloadStoreTypeIds   = _payloadStoreTypeIds;
			for (var i = 0; i < _payloadStoreTypeCount; i++)
			{
				int typeId = payloadStoreTypeIds![i];
				payloadStoresByTypeId![typeId]?.Dispose();
				payloadStoresByTypeId[typeId] = null;
				payloadStoreTypeIds[i]        = 0;
			}

			_payloadStoreTypeCount = 0;
			if (_commands is { } commands)
				ArrayPool<RecordedCommand>.Shared.Return(commands);
			_commands = null;
			if (_resolvedTemporaryEntities is { })
				ArrayPool<Entity>.Shared.Return(_resolvedTemporaryEntities);

			if (_resolvedTemporaryGenerationByIndex is { })
				ArrayPool<int>.Shared.Return(_resolvedTemporaryGenerationByIndex);

			if (_batchEntityIds is { })
				ArrayPool<int>.Shared.Return(_batchEntityIds);

			if (_batchPayloadIndices is { })
				ArrayPool<int>.Shared.Return(_batchPayloadIndices);

			if (_batchEntityMarkerBits is { })
				ArrayPool<uint>.Shared.Return(_batchEntityMarkerBits);

			if (_batchTouchedMarkerWordIndices is { })
				ArrayPool<int>.Shared.Return(_batchTouchedMarkerWordIndices);

			if (_payloadStoresByTypeId is { })
				ArrayPool<ICommandPayloadStore?>.Shared.Return(_payloadStoresByTypeId);

			if (_payloadStoreTypeIds is { })
				ArrayPool<int>.Shared.Return(_payloadStoreTypeIds);

			_resolvedTemporaryEntities          = null;
			_resolvedTemporaryGenerationByIndex = null;
			_batchEntityIds                     = null;
			_batchPayloadIndices                = null;
			_batchEntityMarkerBits              = null;
			_batchTouchedMarkerWordIndices      = null;
			_payloadStoresByTypeId              = null;
			_payloadStoreTypeIds                = null;
			_disposed                           = true;
		}
	}

	/// <summary>
	///     Records a deferred component removal command.
	/// </summary>
	/// <remarks>
	///     If the stream is full the command is silently dropped and the overflow counter is incremented.
	///     Use <see cref="GetDiagnostics" /> to detect lost commands.
	/// </remarks>
	/// <typeparam name="T">Component type to remove.</typeparam>
	/// <param name="entity">Entity to modify.</param>
	public void Remove<T>(Entity entity) where T : struct
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!CanRecordCommand())
				return;

			int typeId = Owner.GetOrCreateComponentTypeId<T>();
			TryRecord(new(RecordedCommandType.RemoveComponent, entity, typeId, -1), out _);
		}
	}

	/// <summary>
	///     Clears recorded commands while retaining allocated buffers.
	/// </summary>
	public void Reset()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			ResetInternal();
		}
	}

	/// <summary>
	///     Records a deferred component set command.
	/// </summary>
	/// <remarks>
	///     If the stream or payload store is full the command is silently dropped and the overflow counter is incremented.
	///     Use <see cref="GetDiagnostics" /> to detect lost commands.
	/// </remarks>
	/// <typeparam name="T">Component value type, including structs containing references.</typeparam>
	/// <param name="entity">Entity to modify.</param>
	/// <param name="component">Component value.</param>
	public void Set<T>(Entity entity, in T component) where T : struct
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!CanRecordCommand())
				return;

			int typeId = Owner.GetOrCreateComponentTypeId<T>();
			var store  = GetOrCreatePayloadStore<T>(typeId);
			if (store.IsFull) _overflowCount++;
			if (!store.TryAdd(in component, out int payloadIndex))
			{
				return;
			}

			TryRecord(new(RecordedCommandType.SetComponent, entity, typeId, payloadIndex), out _);
		}
	}


	internal void PlaybackInternal()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_isPlayingBack)
				throw new InvalidOperationException("Command stream playback is already in progress.");

			if (_commandCount == 0)
				return;

			_isPlayingBack = true;
			try
			{
				var index = 0;
				while (index < _commandCount)
				{
					var command = _commands![index];
					switch (command.Type)
					{
						case RecordedCommandType.CreateEntity:
						{
							EnsureTemporaryResolutionBuffers();
							var   resolvedTemporaryEntities          = _resolvedTemporaryEntities!;
							int[] resolvedTemporaryGenerationByIndex = _resolvedTemporaryGenerationByIndex!;
							var   resolved                           = Owner.CreateEntityInternal();
							int   temporaryIndex                     = GetTemporaryEntityIndex(command.Entity.Id);
							resolvedTemporaryEntities[temporaryIndex]          = resolved;
							resolvedTemporaryGenerationByIndex[temporaryIndex] = _temporaryResolveGeneration;
							index++;
							break;
						}
						case RecordedCommandType.DestroyEntity:
							Owner.DestroyEntityInternal(ResolveEntity(command.Entity));
							index++;
							break;
						case RecordedCommandType.SetComponent:
							index = PlaybackSetRun(index);
							break;
						case RecordedCommandType.RemoveComponent:
							index = PlaybackRemoveRun(index);
							break;
						case RecordedCommandType.CreateEntityWithComponent:
						{
							EnsureTemporaryResolutionBuffers();
							var   resolvedTemporaryEntities          = _resolvedTemporaryEntities!;
							int[] resolvedTemporaryGenerationByIndex = _resolvedTemporaryGenerationByIndex!;
							var   created                            = Owner.CreateEntityInternal();
							int   temporaryIndex                     = GetTemporaryEntityIndex(command.Entity.Id);
							resolvedTemporaryEntities[temporaryIndex]          = created;
							resolvedTemporaryGenerationByIndex[temporaryIndex] = _temporaryResolveGeneration;

							ValidatePayloadStoreTypeId(command.ComponentTypeId);
							var payloadStoresByTypeId = _payloadStoresByTypeId;
							var payloadStore = payloadStoresByTypeId is { }
												   ? payloadStoresByTypeId[command.ComponentTypeId]
												   : null;

							if (payloadStore is null)
								throw new InvalidOperationException(
									$"Missing payload store for component type id '{command.ComponentTypeId}'."
								);

							payloadStore.Apply(Owner, created, command.PayloadIndex);
							index++;
							break;
						}
						default:
							throw new ArgumentOutOfRangeException();
					}
				}
			}
			finally
			{
				_isPlayingBack = false;
				ResetInternal();
			}
		}
	}

	private bool CanRecordCommand()
	{
		if (_commandCount < _commandCapacity)
			return true;

		_overflowCount++;
		if (_overflowPolicy == WorldOverflowPolicy.FailFast)
			throw new InvalidOperationException(
				$"Command capacity '{_commandCapacity}' exceeded for this command stream."
			);

		return false;
	}

	private bool TryMarkBatchEntity(int entityId)
	{
		if (entityId < 0)
			return false;

		uint[]? markerBits               = _batchEntityMarkerBits;
		int[]?  touchedMarkerWordIndices = _batchTouchedMarkerWordIndices;
		if (markerBits is null || touchedMarkerWordIndices is null)
			throw new InvalidOperationException("Batch marker buffers are not initialized.");

		if (unchecked((uint)entityId) >= unchecked((uint)Owner.EntityCapacity))
			return false;

		int  wordIndex  = entityId >> 5;
		uint bitMask    = 1u << (entityId & 31);
		uint markerWord = markerBits[wordIndex];
		if ((markerWord & bitMask) != 0u)
			return false;

		if (markerWord == 0u)
			touchedMarkerWordIndices[_batchTouchedMarkerWordCount++] = wordIndex;

		markerBits[wordIndex] = markerWord | bitMask;
		return true;
	}

	private bool TryRecord(RecordedCommand command, out RecordedCommand recordedCommand)
	{
		if (_commandCount >= _commandCapacity)
		{
			_overflowCount++;
			if (_overflowPolicy == WorldOverflowPolicy.FailFast)
				throw new InvalidOperationException(
					$"Command capacity '{_commandCapacity}' exceeded for this command stream."
				);

			recordedCommand = default;
			return false;
		}

		_commands ??= ArrayPool<RecordedCommand>.Shared.Rent(_commandCapacity);
		recordedCommand             = command;
		_commands[_commandCount++] = command;
		if (_commandCount > _highWatermark)
			_highWatermark = _commandCount;

		return true;
	}

	private CommandPayloadStore<T> GetOrCreatePayloadStore<T>(int typeId) where T : struct
	{
		ValidatePayloadStoreTypeId(typeId);
		EnsurePayloadStoreTables();
		var   payloadStoresByTypeId = _payloadStoresByTypeId!;
		int[] payloadStoreTypeIds   = _payloadStoreTypeIds!;

		var existing = payloadStoresByTypeId[typeId];
		if (existing is { })
		{
			if (existing is not CommandPayloadStore<T> typedStore)
				throw new InvalidOperationException(
					$"Payload store type mismatch for component type id '{typeId}'."
				);

			return typedStore;
		}

		var store = new CommandPayloadStore<T>(_payloadCapacityPerType, _overflowPolicy);
		payloadStoresByTypeId[typeId]                 = store;
		payloadStoreTypeIds[_payloadStoreTypeCount++] = typeId;
		return store;
	}

	private Entity ResolveEntity(Entity entity)
	{
		if (entity.Id >= 0)
			return entity;
		if (entity.Version != _temporaryResolveGeneration)
			throw new InvalidOperationException("Temporary entity belongs to another command stream or recording generation.");

		int    index                              = GetTemporaryEntityIndex(entity.Id);
		int[]? resolvedTemporaryGenerationByIndex = _resolvedTemporaryGenerationByIndex;
		var    resolvedTemporaryEntities          = _resolvedTemporaryEntities;
		if (resolvedTemporaryGenerationByIndex is null || resolvedTemporaryEntities is null)
			throw new InvalidOperationException(
				$"Temporary entity '{entity.Id}' cannot be resolved: no CreateEntity command has been recorded yet."
			);

		if (resolvedTemporaryGenerationByIndex[index] != _temporaryResolveGeneration)
			throw new InvalidOperationException(
				$"Temporary entity '{entity.Id}' cannot be resolved: it was created in a previous playback generation and is no longer valid."
			);

		return resolvedTemporaryEntities[index];
	}

	private int GetTemporaryEntityIndex(int temporaryEntityId)
	{
		long index = -(long)temporaryEntityId - 3;
		if (index < 0 || index >= _commandCapacity)
			throw new InvalidOperationException($"Invalid temporary entity id '{temporaryEntityId}'.");

		return (int)index;
	}

	private int PlaybackRemoveRun(int startIndex)
	{
		EnsureBatchEntityBuffers();
		int[]  batchEntityIds        = _batchEntityIds!;
		uint[] batchEntityMarkerBits = _batchEntityMarkerBits!;
		var    first                 = _commands![startIndex];
		var    firstEntity           = ResolveEntity(first.Entity);
		Owner.DescribeRemoveTransition(
			firstEntity,
			first.ComponentTypeId,
			out int sourceArchetypeId,
			out int targetArchetypeId
		);

		BeginBatchEntityMarkerRun();
		int readIndex = startIndex;
		var runCount  = 0;
		while (readIndex < _commandCount)
		{
			var command = _commands![readIndex];
			if (command.Type != RecordedCommandType.RemoveComponent ||
				command.ComponentTypeId != first.ComponentTypeId)
				break;

			var resolved = ResolveEntity(command.Entity);
			if (!TryMarkBatchEntity(resolved.Id))
				break;

			if (!Owner.MatchesRemoveTransitionSource(
					resolved,
					sourceArchetypeId,
					command.ComponentTypeId,
					targetArchetypeId
				))
				break;

			batchEntityIds[runCount++] = resolved.Id;
			readIndex++;
		}

		if (runCount > 0)
		{
			int archetypeEntityCount = Owner.GetArchetypeEntityCount(sourceArchetypeId);
			if (runCount == archetypeEntityCount)
				Owner.RemoveAllComponentsFromCommandKnownTransitionFast(
					sourceArchetypeId,
					targetArchetypeId
				);
			else if (runCount << 2 >= archetypeEntityCount)
				Owner.RemoveMarkedComponentsFromCommandKnownTransitionFast(
					batchEntityMarkerBits,
					sourceArchetypeId,
					targetArchetypeId
				);
			else
				Owner.RemoveComponentBatchFromCommandKnownTransitionFast(
					batchEntityIds,
					0,
					runCount,
					sourceArchetypeId,
					targetArchetypeId
				);
		}

		return readIndex;
	}

	private int PlaybackSetRun(int startIndex)
	{
		EnsureBatchEntityBuffers();
		EnsureBatchPayloadIndexBuffer();
		int[] batchEntityIds      = _batchEntityIds!;
		int[] batchPayloadIndices = _batchPayloadIndices!;
		var   first               = _commands![startIndex];
		ValidatePayloadStoreTypeId(first.ComponentTypeId);
		var payloadStoresByTypeId = _payloadStoresByTypeId;
		var payloadStore = payloadStoresByTypeId is { }
							   ? payloadStoresByTypeId[first.ComponentTypeId]
							   : null;

		if (payloadStore is null)
			throw new InvalidOperationException(
				$"Missing payload store for component type id '{first.ComponentTypeId}'."
			);

		var firstEntity = ResolveEntity(first.Entity);
		Owner.DescribeSetTransition(
			firstEntity,
			first.ComponentTypeId,
			out int sourceArchetypeId,
			out int targetArchetypeId
		);

		BeginBatchEntityMarkerRun();
		int readIndex = startIndex;
		var runCount  = 0;
		while (readIndex < _commandCount)
		{
			var command = _commands![readIndex];
			if (command.Type != RecordedCommandType.SetComponent ||
				command.ComponentTypeId != first.ComponentTypeId)
				break;

			var resolved = ResolveEntity(command.Entity);
			if (!TryMarkBatchEntity(resolved.Id))
				break;

			if (!Owner.MatchesSetTransitionSource(
					resolved,
					sourceArchetypeId,
					command.ComponentTypeId,
					targetArchetypeId
				))
				break;

			batchEntityIds[runCount]      = resolved.Id;
			batchPayloadIndices[runCount] = command.PayloadIndex;
			runCount++;
			readIndex++;
		}

		payloadStore.ApplyBatch(
			Owner,
			batchEntityIds,
			0,
			runCount,
			batchPayloadIndices,
			0,
			first.ComponentTypeId,
			sourceArchetypeId,
			targetArchetypeId
		);

		return readIndex;
	}

	private void BeginBatchEntityMarkerRun()
	{
		uint[]? markerBits               = _batchEntityMarkerBits;
		int[]?  touchedMarkerWordIndices = _batchTouchedMarkerWordIndices;
		if (markerBits is null || touchedMarkerWordIndices is null)
			throw new InvalidOperationException("Batch marker buffers are not initialized.");

		for (var i = 0; i < _batchTouchedMarkerWordCount; i++)
		{
			int wordIndex = touchedMarkerWordIndices[i];
			markerBits[wordIndex] = 0u;
		}

		_batchTouchedMarkerWordCount = 0;
	}

	private void EnsureTemporaryGeneration()
	{
		if (_temporaryResolveGeneration != 0) return;
		long observed = Volatile.Read(ref _nextTemporaryGeneration);
		while (true)
		{
			if (observed < 0 || observed >= uint.MaxValue)
				throw new InvalidOperationException("The process has exhausted temporary entity identities; identities cannot be recycled safely.");
			long next = observed + 1;
			long previous = Interlocked.CompareExchange(ref _nextTemporaryGeneration, next, observed);
			if (previous == observed)
			{
				_temporaryResolveGeneration = ValidateTemporaryGeneration(next);
				return;
			}
			observed = previous;
		}
	}

	internal static int ValidateTemporaryGeneration(long generation)
	{
		if (generation <= 0 || generation > uint.MaxValue)
			throw new InvalidOperationException("The process has exhausted temporary entity identities; identities cannot be recycled safely.");
		return unchecked((int)generation);
	}

	private void EnsureBatchEntityBuffers()
	{
		if (_batchEntityIds is null)
			_batchEntityIds = ArrayPool<int>.Shared.Rent(_commandCapacity);

		if (_batchEntityMarkerBits is not null && _batchTouchedMarkerWordIndices is not null)
			return;

		int markerWordCount = (Owner.EntityCapacity + 31) >> 5;
		_batchEntityMarkerBits = ArrayPool<uint>.Shared.Rent(markerWordCount);
		Array.Clear(_batchEntityMarkerBits, 0, markerWordCount);
		_batchTouchedMarkerWordIndices = ArrayPool<int>.Shared.Rent(markerWordCount);
		_batchTouchedMarkerWordCount   = 0;
	}

	private void EnsureBatchPayloadIndexBuffer()
	{
		if (_batchPayloadIndices is null)
			_batchPayloadIndices = ArrayPool<int>.Shared.Rent(_commandCapacity);
	}

	private void EnsurePayloadStoreTables()
	{
		if (_payloadStoresByTypeId is { })
			return;

		_payloadStoresByTypeId = ArrayPool<ICommandPayloadStore?>.Shared.Rent(_componentTypeCapacity);
		Array.Clear(_payloadStoresByTypeId, 0, _payloadStoresByTypeId.Length);
		_payloadStoreTypeIds = ArrayPool<int>.Shared.Rent(_componentTypeCapacity);
	}

	private void EnsureTemporaryResolutionBuffers()
	{
		if (_resolvedTemporaryEntities is { })
			return;

		_resolvedTemporaryEntities          = ArrayPool<Entity>.Shared.Rent(_commandCapacity);
		_resolvedTemporaryGenerationByIndex = ArrayPool<int>.Shared.Rent(_commandCapacity);
		Array.Clear(_resolvedTemporaryGenerationByIndex, 0, _resolvedTemporaryGenerationByIndex.Length);
	}

	private void ResetInternal()
	{
		_commandCount    = 0;
		_nextTemporaryId = -3;
		_temporaryResolveGeneration = 0;
		var    payloadStoresByTypeId = _payloadStoresByTypeId;
		int[]? payloadStoreTypeIds   = _payloadStoreTypeIds;
		if (payloadStoresByTypeId is null || payloadStoreTypeIds is null)
			return;

		for (var i = 0; i < _payloadStoreTypeCount; i++)
		{
			int typeId = payloadStoreTypeIds[i];
			payloadStoresByTypeId[typeId]?.Clear();
		}
	}

	private void ThrowIfDisposed()
	{
		if (_disposed)
			throw new ObjectDisposedException(nameof(CommandStream));
	}

	private void ValidatePayloadStoreTypeId(int typeId)
	{
		if (unchecked((uint)typeId) >= unchecked((uint)_componentTypeCapacity))
			throw new InvalidOperationException($"Invalid component type id '{typeId}' for this command stream.");
	}
}

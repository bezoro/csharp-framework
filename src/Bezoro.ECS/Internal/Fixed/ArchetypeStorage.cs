using System.Runtime.CompilerServices;

namespace Bezoro.ECS.Internal.Fixed;

internal sealed class ArchetypeStorage : IDisposable
{
	private const int UNKNOWN_TRANSITION = int.MinValue;

	private readonly bool[] _columnIsManagedLane;
	private readonly int    _chunkCapacity;
	private readonly int[]  _addTransitionByTypeId;
	private readonly int[]  _columnIndexByTypeId;
	private readonly int[]  _removeTransitionByTypeId;
	private readonly Type[] _columnTypes;

	private Chunk[] _chunks;
	private int     _firstAvailableChunkIndex;

	public ArchetypeStorage(
		int     id,
		int[]   typeIds,
		ulong[] maskWords,
		Type[]  columnTypes,
		bool[]  columnIsManagedLane,
		int     componentTypeCapacity,
		int     chunkCapacity)
	{
		Id                   = id;
		TypeIds              = typeIds;
		MaskWords            = maskWords;
		_columnTypes         = columnTypes;
		_columnIsManagedLane = columnIsManagedLane;
		_chunkCapacity       = chunkCapacity;

		_columnIndexByTypeId = new int[componentTypeCapacity];
		Array.Fill(_columnIndexByTypeId, -1);
		for (var i = 0; i < typeIds.Length; i++)
			_columnIndexByTypeId[typeIds[i]] = i;

		_addTransitionByTypeId    = new int[componentTypeCapacity];
		_removeTransitionByTypeId = new int[componentTypeCapacity];
		Array.Fill(_addTransitionByTypeId,    UNKNOWN_TRANSITION);
		Array.Fill(_removeTransitionByTypeId, UNKNOWN_TRANSITION);

		_chunks = new Chunk[4];
	}

	public int Id { get; }

	public int[] TypeIds { get; }

	public ulong[] MaskWords { get; }

	public int ChunkCount { get; private set; }

	public int EntityCount { get; private set; }

	public bool HasType(int typeId) =>
		unchecked((uint)typeId) < unchecked((uint)_columnIndexByTypeId.Length) &&
		_columnIndexByTypeId[typeId] >= 0;

	public bool RemoveAt(int chunkIndex, int rowIndex, out int movedEntityId)
	{
		var chunk = GetChunk(chunkIndex);
		if (unchecked((uint)rowIndex) >= unchecked((uint)chunk.Count))
			throw new ArgumentOutOfRangeException(nameof(rowIndex));

		int lastRowIndex = chunk.Count - 1;
		movedEntityId = -1;
		if (rowIndex != lastRowIndex)
		{
			movedEntityId             = chunk.EntityIds[lastRowIndex];
			chunk.EntityIds[rowIndex] = movedEntityId;

			for (var c = 0; c < chunk.Columns.Length; c++)
				chunk.Columns[c].CopyElementTo(lastRowIndex, chunk.Columns[c], rowIndex);
		}

		for (var c = 0; c < chunk.Columns.Length; c++)
		{
			if (_columnIsManagedLane[c])
				chunk.Columns[c].Clear(lastRowIndex, 1);
		}

		chunk.EntityIds[lastRowIndex] = 0;
		chunk.Count--;
		EntityCount--;
		if (chunk.Count < _chunkCapacity && chunkIndex < _firstAvailableChunkIndex)
			_firstAvailableChunkIndex = chunkIndex;

		return movedEntityId >= 0;
	}

	public bool TryGetColumnIndex(int typeId, out int columnIndex)
	{
		if (unchecked((uint)typeId) < unchecked((uint)_columnIndexByTypeId.Length))
		{
			columnIndex = _columnIndexByTypeId[typeId];
			return columnIndex >= 0;
		}

		columnIndex = -1;
		return false;
	}

	public bool TryGetValue<T>(int chunkIndex, int rowIndex, int typeId, out T value) where T : struct
	{
		value = default;
		int columnIndex = GetColumnIndexOrNegative(typeId);
		if (columnIndex < 0)
			return false;

		var chunk = GetChunk(chunkIndex);
		if (unchecked((uint)rowIndex) >= unchecked((uint)chunk.Count))
			return false;

		value = chunk.Columns[columnIndex].GetReference<T>(rowIndex);
		return true;
	}

	public Chunk GetChunk(int chunkIndex)
	{
		if (unchecked((uint)chunkIndex) >= unchecked((uint)ChunkCount))
			throw new ArgumentOutOfRangeException(nameof(chunkIndex));

		return _chunks[chunkIndex];
	}

	public Chunk GetChunkUnchecked(int chunkIndex) => _chunks[chunkIndex];

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int GetColumnIndexOrNegative(int typeId) =>
		unchecked((uint)typeId) < unchecked((uint)_columnIndexByTypeId.Length)
			? _columnIndexByTypeId[typeId]
			: -1;

	public int GetKnownAddTransition(int typeId) =>
		unchecked((uint)typeId) < unchecked((uint)_addTransitionByTypeId.Length)
			? _addTransitionByTypeId[typeId]
			: UNKNOWN_TRANSITION;

	public int GetKnownRemoveTransition(int typeId) =>
		unchecked((uint)typeId) < unchecked((uint)_removeTransitionByTypeId.Length)
			? _removeTransitionByTypeId[typeId]
			: UNKNOWN_TRANSITION;

	public int ReserveRows(int requestedCount, out int chunkIndex, out int rowStart)
	{
		if (requestedCount <= 0)
			throw new ArgumentOutOfRangeException(nameof(requestedCount));

		for (int i = _firstAvailableChunkIndex; i < ChunkCount; i++)
		{
			var candidate = _chunks[i];
			int available = _chunkCapacity - candidate.Count;
			if (available <= 0)
				continue;

			rowStart = candidate.Count;
			int reserved = Math.Min(requestedCount, available);
			candidate.Count += reserved;
			EntityCount     += reserved;
			chunkIndex      =  i;
			if (candidate.Count >= _chunkCapacity && i == _firstAvailableChunkIndex)
			{
				_firstAvailableChunkIndex = i + 1;
				while (_firstAvailableChunkIndex < ChunkCount &&
					   _chunks[_firstAvailableChunkIndex].Count >= _chunkCapacity)
					_firstAvailableChunkIndex++;
			}

			return reserved;
		}

		if (ChunkCount == _chunks.Length)
			Array.Resize(ref _chunks, _chunks.Length * 2);

		var chunk = new Chunk(_chunkCapacity, _columnTypes);
		_chunks[ChunkCount] = chunk;
		chunkIndex          = ChunkCount++;
		rowStart            = 0;
		int reservedRows = Math.Min(requestedCount, _chunkCapacity);
		chunk.Count               =  reservedRows;
		EntityCount               += reservedRows;
		_firstAvailableChunkIndex =  chunkIndex;
		if (chunk.Count >= _chunkCapacity)
		{
			_firstAvailableChunkIndex++;
			while (_firstAvailableChunkIndex < ChunkCount &&
				   _chunks[_firstAvailableChunkIndex].Count >= _chunkCapacity)
				_firstAvailableChunkIndex++;
		}

		return reservedRows;
	}

	public ReadOnlySpan<T> GetReadOnlySpanByIndex<T>(Chunk chunk, int columnIndex) where T : struct
	{
		if (unchecked((uint)columnIndex) >= unchecked((uint)chunk.Columns.Length))
			throw new ArgumentOutOfRangeException(nameof(columnIndex));

		return chunk.Columns[columnIndex].GetReadOnlySpan<T>(chunk.Count);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public ulong GetComponentChangeVersion(Chunk chunk, int columnIndex)
	{
		if (unchecked((uint)columnIndex) >= unchecked((uint)chunk.ComponentChangeVersions.Length))
			throw new ArgumentOutOfRangeException(nameof(columnIndex));

		return chunk.ComponentChangeVersions[columnIndex];
	}

	public Span<T> GetSpanByIndex<T>(Chunk chunk, int columnIndex) where T : struct
	{
		if (unchecked((uint)columnIndex) >= unchecked((uint)chunk.Columns.Length))
			throw new ArgumentOutOfRangeException(nameof(columnIndex));

		return chunk.Columns[columnIndex].GetSpan<T>(chunk.Count);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void MarkComponentChanged(Chunk chunk, int columnIndex, ulong version)
	{
		if (unchecked((uint)columnIndex) >= unchecked((uint)chunk.ComponentChangeVersions.Length))
			throw new ArgumentOutOfRangeException(nameof(columnIndex));

		chunk.ComponentChangeVersions[columnIndex] = version;
	}

	public ref T GetRef<T>(int chunkIndex, int rowIndex, int typeId) where T : struct
	{
		int columnIndex = GetColumnIndexOrNegative(typeId);
		if (columnIndex < 0)
			throw new KeyNotFoundException($"Type id '{typeId}' does not exist in archetype '{Id}'.");

		var chunk = GetChunk(chunkIndex);
		if (unchecked((uint)rowIndex) >= unchecked((uint)chunk.Count))
			throw new ArgumentOutOfRangeException(nameof(rowIndex));

		return ref chunk.Columns[columnIndex].GetReference<T>(rowIndex);
	}

	public ref T GetRefByIndex<T>(Chunk chunk, int columnIndex, int rowIndex) where T : struct
	{
		if (unchecked((uint)columnIndex) >= unchecked((uint)chunk.Columns.Length))
			throw new ArgumentOutOfRangeException(nameof(columnIndex));

		if (unchecked((uint)rowIndex) >= unchecked((uint)chunk.Count))
			throw new ArgumentOutOfRangeException(nameof(rowIndex));

		return ref chunk.Columns[columnIndex].GetReference<T>(rowIndex);
	}

	public void AllocateRow(int entityId, out int chunkIndex, out int rowIndex)
	{
		_                                       = ReserveRows(1, out chunkIndex, out rowIndex);
		_chunks[chunkIndex].EntityIds[rowIndex] = entityId;
	}

	public void Clear()
	{
		for (var chunkIndex = 0; chunkIndex < ChunkCount; chunkIndex++)
		{
			var chunk = _chunks[chunkIndex];
			if (chunk.Count == 0)
				continue;

			for (var columnIndex = 0; columnIndex < chunk.Columns.Length; columnIndex++)
			{
				if (_columnIsManagedLane[columnIndex])
					chunk.Columns[columnIndex].Clear(0, chunk.Count);
			}

			Array.Clear(chunk.EntityIds, 0, chunk.Count);
			Array.Clear(chunk.ComponentChangeVersions, 0, chunk.ComponentChangeVersions.Length);
			chunk.Count = 0;
		}

		EntityCount               = 0;
		_firstAvailableChunkIndex = 0;
	}

	public void ClearChunk(int chunkIndex)
	{
		var chunk = GetChunk(chunkIndex);
		if (chunk.Count == 0)
			return;

		int count = chunk.Count;
		for (var c = 0; c < chunk.Columns.Length; c++)
		{
			if (_columnIsManagedLane[c])
				chunk.Columns[c].Clear(0, count);
		}

		Array.Clear(chunk.EntityIds, 0, count);
		Array.Clear(chunk.ComponentChangeVersions, 0, chunk.ComponentChangeVersions.Length);
		chunk.Count =  0;
		EntityCount -= count;
		if (chunkIndex < _firstAvailableChunkIndex)
			_firstAvailableChunkIndex = chunkIndex;
	}

	public void CopySharedColumnsFrom(
		ArchetypeStorage sourceArchetype,
		Chunk            sourceChunk,
		int              sourceRowIndex,
		Chunk            targetChunk,
		int              targetRowIndex)
	{
		for (var i = 0; i < TypeIds.Length; i++)
		{
			int typeId            = TypeIds[i];
			int sourceColumnIndex = sourceArchetype.GetColumnIndexOrNegative(typeId);
			if (sourceColumnIndex < 0)
				continue;

			sourceChunk.Columns[sourceColumnIndex].CopyElementTo(
				sourceRowIndex,
				targetChunk.Columns[i],
				targetRowIndex
			);

			ulong sourceVersion = sourceChunk.ComponentChangeVersions[sourceColumnIndex];
			if (targetChunk.ComponentChangeVersions[i] < sourceVersion)
				targetChunk.ComponentChangeVersions[i] = sourceVersion;
		}
	}

	public void CopySharedColumnsFromWithPairs(
		Chunk sourceChunk,
		int   sourceRowIndex,
		Chunk targetChunk,
		int   targetRowIndex,
		int[] sourceTargetColumnPairs)
	{
		CopySharedColumnsFromWithPairs(
			sourceChunk,
			sourceRowIndex,
			targetChunk,
			targetRowIndex,
			1,
			sourceTargetColumnPairs
		);
	}

	public void CopySharedColumnsFromWithPairs(
		Chunk sourceChunk,
		int   sourceRowIndex,
		Chunk targetChunk,
		int   targetRowIndex,
		int   rowCount,
		int[] sourceTargetColumnPairs)
	{
		if (rowCount == 0)
			return;

		for (var i = 0; i < sourceTargetColumnPairs.Length; i += 2)
		{
			int sourceColumnIndex = sourceTargetColumnPairs[i];
			int targetColumnIndex = sourceTargetColumnPairs[i + 1];

			sourceChunk.Columns[sourceColumnIndex].CopyRangeTo(
				sourceRowIndex,
				targetChunk.Columns[targetColumnIndex],
				targetRowIndex,
				rowCount
			);

			ulong sourceVersion = sourceChunk.ComponentChangeVersions[sourceColumnIndex];
			if (targetChunk.ComponentChangeVersions[targetColumnIndex] < sourceVersion)
				targetChunk.ComponentChangeVersions[targetColumnIndex] = sourceVersion;
		}
	}

	public void Dispose()
	{
		for (var i = 0; i < ChunkCount; i++)
			_chunks[i].DisposeColumns();

		_chunks                   = [];
		ChunkCount                = 0;
		_firstAvailableChunkIndex = 0;
		EntityCount               = 0;
	}

	public void FinalizeChunkCompaction(int chunkIndex, int newCount)
	{
		var chunk    = GetChunk(chunkIndex);
		int oldCount = chunk.Count;
		if (unchecked((uint)newCount) > unchecked((uint)oldCount))
			throw new ArgumentOutOfRangeException(nameof(newCount));

		if (newCount == oldCount)
			return;

		int removedCount = oldCount - newCount;
		for (var c = 0; c < chunk.Columns.Length; c++)
		{
			if (_columnIsManagedLane[c])
				chunk.Columns[c].Clear(newCount, removedCount);
		}

		Array.Clear(chunk.EntityIds, newCount, removedCount);
		if (newCount == 0)
			Array.Clear(chunk.ComponentChangeVersions, 0, chunk.ComponentChangeVersions.Length);

		chunk.Count =  newCount;
		EntityCount -= removedCount;
		if (newCount < _chunkCapacity && chunkIndex < _firstAvailableChunkIndex)
			_firstAvailableChunkIndex = chunkIndex;
	}

	public void MoveRowRangeWithinChunk(Chunk chunk, int sourceRowIndex, int destinationRowIndex, int rowCount)
	{
		if (rowCount == 0 || sourceRowIndex == destinationRowIndex)
			return;

		if (sourceRowIndex < 0 || sourceRowIndex + rowCount > chunk.Count)
			throw new ArgumentOutOfRangeException(nameof(sourceRowIndex));

		if (destinationRowIndex < 0 || destinationRowIndex + rowCount > chunk.Count)
			throw new ArgumentOutOfRangeException(nameof(destinationRowIndex));

		Array.Copy(chunk.EntityIds, sourceRowIndex, chunk.EntityIds, destinationRowIndex, rowCount);
		for (var i = 0; i < chunk.Columns.Length; i++)
		{
			chunk.Columns[i].CopyRangeTo(
				sourceRowIndex,
				chunk.Columns[i],
				destinationRowIndex,
				rowCount
			);
		}
	}

	public void SetKnownAddTransition(int typeId, int targetArchetypeId)
	{
		if (unchecked((uint)typeId) >= unchecked((uint)_addTransitionByTypeId.Length))
			return;

		_addTransitionByTypeId[typeId] = targetArchetypeId;
	}

	public void SetKnownRemoveTransition(int typeId, int targetArchetypeId)
	{
		if (unchecked((uint)typeId) >= unchecked((uint)_removeTransitionByTypeId.Length))
			return;

		_removeTransitionByTypeId[typeId] = targetArchetypeId;
	}

	internal sealed class Chunk
	{
		public Chunk(int chunkCapacity, Type[] columnTypes)
		{
			EntityIds                = new int[chunkCapacity];
			Columns                  = new ComponentColumn[columnTypes.Length];
			ComponentChangeVersions = new ulong[columnTypes.Length];
			int initialized = 0;
			try
			{
				for (; initialized < columnTypes.Length; initialized++)
					Columns[initialized] = ComponentColumn.Create(columnTypes[initialized], chunkCapacity);
			}
			catch
			{
				for (var i = 0; i < initialized; i++) Columns[i].Dispose();
				throw;
			}
		}

		public ComponentColumn[] Columns { get; }

		public ulong[] ComponentChangeVersions { get; }

		public int[] EntityIds { get; }

		public int Count { get; set; }

		public void DisposeColumns()
		{
			for (var i = 0; i < Columns.Length; i++)
				Columns[i].Dispose();
		}
	}
}

using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Options;

namespace Bezoro.ECS.Internal;

internal sealed class WorldSnapshotCoordinator(
	WorldEntityStore      entityStore,
	WorldLifecycleService lifecycleService,
	WorldQueryEngine      queryEngine,
	WorldSnapshotService  snapshotService
)
{
	private readonly WorldEntityStore      _entityStore      = entityStore;
	private readonly WorldLifecycleService _lifecycleService = lifecycleService;
	private readonly WorldQueryEngine      _queryEngine      = queryEngine;
	private readonly WorldSnapshotService  _snapshotService  = snapshotService;

	public void Capture<TSnapshotWriter>(ref TSnapshotWriter writer)
		where TSnapshotWriter : struct, IWorldSnapshotWriter
	{
		EnsureNoActiveQueryIterations("Snapshot capture cannot run while a query iteration is active.");
		_queryEngine.EnterQueryIteration();
		try { _snapshotService.Capture(ref writer, _entityStore.AliveCount, _entityStore.NextEntityId); }
		finally { _queryEngine.ExitQueryIteration(); }
	}

	public void Restore<TSnapshotReader>(
		ref TSnapshotReader             reader,
		SnapshotDeserializationOptions? options)
		where TSnapshotReader : struct, IWorldSnapshotReader
	{
		EnsureNoActiveQueryIterations("Snapshot restore cannot run while a query iteration is active.");

		options ??= SnapshotDeserializationOptions.Default;
		SnapshotRestorePlan restorePlan;
		_queryEngine.EnterQueryIteration();
		try
		{
			var snapshot = reader.Read() ?? throw new InvalidOperationException("Snapshot reader returned null payload.");
			restorePlan = _snapshotService.ValidateRestorePlan(snapshot, options);
		}
		finally { _queryEngine.ExitQueryIteration(); }
		EnsureNoActiveQueryIterations("Snapshot restore cannot run while a query iteration is active.");
		try
		{
			_lifecycleService.Clear();
			_snapshotService.ApplyRestorePlan(restorePlan);
		}
		catch
		{
			_lifecycleService.Clear();
			throw;
		}
	}

	private void EnsureNoActiveQueryIterations(string message)
	{
		if (_queryEngine.HasActiveQueryIterations)
			throw new InvalidOperationException(message);
	}
}

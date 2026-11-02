using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;

namespace Bezoro.Chess.UCI.Protocol.Internal;

internal sealed class MoveClassificationCoordinator : IDisposable
{
	private static readonly ImmutableDictionary<string, MoveClassification> EmptyClassifications =
		ImmutableDictionary<string, MoveClassification>.Empty.WithComparers(StringComparer.Ordinal);
	private readonly Func<Fen, ImmutableArray<string>, CancellationToken,
		ImmutableDictionary<string, MoveClassification>> _classifyFully;
	private readonly Action? _beforeWorkerRetires;
	private readonly Dictionary<string, ImmutableDictionary<string, MoveClassification>> _classificationsByPosition =
		new(StringComparer.Ordinal);
	private readonly Dictionary<string, TaskCompletionSource<ImmutableDictionary<string, MoveClassification>>> _waiters =
		new(StringComparer.Ordinal);
	private readonly HashSet<string>                      _queuedPositionKeys = new(StringComparer.Ordinal);
	private readonly object                               _sync               = new();
	private readonly Queue<PendingClassificationPosition> _pendingPositions   = new();
	private          long                                 _generation;
	private          long                                 _knownVersion;
	private          WorkerCancellationSource?            _workerCancellation;
	private          long                                 _workerGeneration;
	private          Task?                                _workerTask;

	public MoveClassificationCoordinator()
		: this(static (fen, moves, ct) => LocalPositionRules.ClassifyMovesFully(fen, moves, ct)) { }

	internal MoveClassificationCoordinator(
		Func<Fen, ImmutableArray<string>, CancellationToken,
			ImmutableDictionary<string, MoveClassification>> classifyFully,
		Action? beforeWorkerRetires = null)
	{
		_classifyFully       = classifyFully;
		_beforeWorkerRetires = beforeWorkerRetires;
	}

	internal Task? ActiveWorker
	{
		get
		{
			lock (_sync)
				return _workerTask;
		}
	}

	public ImmutableDictionary<string, MoveClassification> GetKnown(string positionKey)
	{
		lock (_sync)
		{
			return _classificationsByPosition.TryGetValue(positionKey, out var classifications)
					   ? classifications
					   : EmptyClassifications;
		}
	}

	internal bool HasKnownPositions
	{
		get { lock (_sync) return _classificationsByPosition.Count != 0; }
	}

	internal long KnownVersion
	{
		get { lock (_sync) return _classificationsByPosition.Count == 0 ? 0 : _knownVersion; }
	}

	public void Enqueue(string positionKey, Fen fen, ImmutableArray<string> legalMoves)
	{
		var structural = fen.ClassifyMoves(legalMoves);

		lock (_sync)
		{
			if (_classificationsByPosition.TryGetValue(positionKey, out var existing))
				structural = MergeClassifications(structural, existing);

			_classificationsByPosition[positionKey] = structural;
			_knownVersion++;

			var waiter = GetOrCreateWaiter(positionKey);
			if (legalMoves.IsDefaultOrEmpty ||
				structural.Values.All(static classification => classification.IsResolved))
			{
				_waiters.Remove(positionKey);
				waiter.TrySetResult(structural);
				return;
			}

			if (!_queuedPositionKeys.Add(positionKey))
				return;

			_pendingPositions.Enqueue(new(positionKey, fen, legalMoves));
			EnsureWorkerStarted();
		}
	}

	public Task<ImmutableDictionary<string, MoveClassification>> WaitAsync(
		string            positionKey,
		CancellationToken ct = default)
	{
		Task<ImmutableDictionary<string, MoveClassification>>? waiterTask;

		lock (_sync)
		{
			waiterTask = _waiters.TryGetValue(positionKey, out var waiter)
							 ? waiter.Task
							 : null;
		}

		return waiterTask is null
				   ? Task.FromResult(GetKnown(positionKey))
				   : WaitWithCancellationAsync(waiterTask, ct);
	}

	public void Cancel() => CancelCore(null);

	public void CancelPendingAndRetain(ISet<string> retainedPositionKeys)
	{
		if (retainedPositionKeys is null)
			throw new ArgumentNullException(nameof(retainedPositionKeys));
		CancelCore(retainedPositionKeys);
	}

	private void CancelCore(ISet<string>? retainedPositionKeys)
	{
		TaskCompletionSource<ImmutableDictionary<string, MoveClassification>>[] waiters;
		WorkerCancellationSource? workerCancellation;

		lock (_sync)
		{
			_generation++;
			workerCancellation = _workerCancellation;
			waiters = [.. _waiters.Values];

			if (retainedPositionKeys is null)
				_classificationsByPosition.Clear();
			else foreach (var positionKey in _classificationsByPosition.Keys.ToArray())
			{
				if (!retainedPositionKeys.Contains(positionKey))
					_classificationsByPosition.Remove(positionKey);
			}

			_waiters.Clear();
			_pendingPositions.Clear();
			_queuedPositionKeys.Clear();
		}

		workerCancellation?.Cancel();

		foreach (var waiter in waiters)
			waiter.TrySetCanceled();
	}

	public void Dispose() => Cancel();

	private static ImmutableDictionary<string, MoveClassification> MergeClassifications(
		ImmutableDictionary<string, MoveClassification> structural,
		ImmutableDictionary<string, MoveClassification> existing)
	{
		if (existing.Count == 0)
			return structural;

		var builder = structural.ToBuilder();
		foreach ((string move, var classification) in existing)
		{
			if (!classification.IsResolved)
				continue;

			if (builder.ContainsKey(move))
				builder[move] = classification;
		}

		return builder.ToImmutable();
	}

	private static async Task<T> WaitWithCancellationAsync<T>(Task<T> task, CancellationToken ct)
	{
#if NET9_0_OR_GREATER
		return await task.WaitAsync(ct).ConfigureAwait(false);
#else
		if (!ct.CanBeCanceled)
			return await task.ConfigureAwait(false);

		var cancellationTask = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = ct.Register(
			static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
			cancellationTask
		);

		if (task != await Task.WhenAny(task, cancellationTask.Task).ConfigureAwait(false))
			throw new OperationCanceledException(ct);

		return await task.ConfigureAwait(false);
#endif
	}

	private void EnsureWorkerStarted()
	{
		if (_workerTask is not null)
			return;

		var generation = _generation;
		var cancellation = new WorkerCancellationSource();
		_workerGeneration   = generation;
		_workerCancellation = cancellation;
		_workerTask         = Task.Run(() => RunWorker(generation, cancellation));
	}

	private TaskCompletionSource<ImmutableDictionary<string, MoveClassification>> GetOrCreateWaiter(string positionKey)
	{
		if (_waiters.TryGetValue(positionKey, out var waiter))
			return waiter;

		waiter               = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_waiters[positionKey] = waiter;
		return waiter;
	}

	private void RunWorker(long generation, WorkerCancellationSource cancellation)
	{
		var ct = cancellation.Token;
		try
		{
			while (true)
			{
				PendingClassificationPosition position = default;
				bool shouldRetire;
				lock (_sync)
				{
					if (_generation != generation)
						return;

					shouldRetire = _pendingPositions.Count == 0;
					if (!shouldRetire)
						position = _pendingPositions.Dequeue();
				}

				if (shouldRetire)
				{
					_beforeWorkerRetires?.Invoke();
					return;
				}

				try
				{
					var completed = Classify(position, generation, ct);
					lock (_sync)
					{
						if (_generation != generation || ct.IsCancellationRequested)
							return;

						_classificationsByPosition[position.PositionKey] = completed;
						_knownVersion++;
						_queuedPositionKeys.Remove(position.PositionKey);
						if (_waiters.Remove(position.PositionKey, out var waiter))
							waiter.TrySetResult(completed);
					}
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					return;
				}
				catch (Exception ex)
				{
					lock (_sync)
					{
						if (_generation != generation)
							return;

						_queuedPositionKeys.Remove(position.PositionKey);
						if (_waiters.Remove(position.PositionKey, out var waiter))
							waiter.TrySetException(ex);
					}

					throw;
				}
			}
		}
		finally
		{
			lock (_sync)
			{
				if (_workerGeneration == generation && ReferenceEquals(_workerCancellation, cancellation))
				{
					_workerCancellation = null;
					_workerTask         = null;
					if (_pendingPositions.Count > 0)
						EnsureWorkerStarted();
				}
			}

			cancellation.Dispose();
		}
	}

	private ImmutableDictionary<string, MoveClassification> Classify(
		PendingClassificationPosition position,
		long                          generation,
		CancellationToken             ct)
	{
		ImmutableDictionary<string, MoveClassification> existing;
		lock (_sync)
		{
			ThrowIfRetired(generation, ct);
			existing = _classificationsByPosition[position.PositionKey];
		}

		var completed = _classifyFully(position.Fen, position.LegalMoves, ct);
		ct.ThrowIfCancellationRequested();
		completed = MergeClassifications(completed, existing);

		lock (_sync)
		{
			ThrowIfRetired(generation, ct);
		}

		return completed;
	}

	private void ThrowIfRetired(long generation, CancellationToken ct)
	{
		if (_generation != generation)
			throw new OperationCanceledException(ct);
	}

	private sealed class WorkerCancellationSource : IDisposable
	{
		private readonly CancellationTokenSource _source = new();
		private readonly object                  _sync = new();
		private          int                     _activeCancellations;
		private          bool                    _disposeRequested;

		public CancellationToken Token => _source.Token;

		public void Cancel()
		{
			lock (_sync)
			{
				if (_disposeRequested)
					return;

				_activeCancellations++;
			}

			try
			{
				_source.Cancel();
			}
			finally
			{
				bool dispose;
				lock (_sync)
				{
					_activeCancellations--;
					dispose = _disposeRequested && _activeCancellations == 0;
				}

				if (dispose)
					_source.Dispose();
			}
		}

		public void Dispose()
		{
			bool dispose;
			lock (_sync)
			{
				if (_disposeRequested)
					return;

				_disposeRequested = true;
				dispose = _activeCancellations == 0;
			}

			if (dispose)
				_source.Dispose();
		}
	}

	private readonly record struct PendingClassificationPosition(
		string                 PositionKey,
		Fen                    Fen,
		ImmutableArray<string> LegalMoves
	);
}

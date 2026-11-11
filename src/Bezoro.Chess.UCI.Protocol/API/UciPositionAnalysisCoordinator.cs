using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.Internal;

namespace Bezoro.Chess.UCI.Protocol.API;

/// <summary>
///     Coordinates ordered full-strength position analysis over a dedicated client.
///     Positions are analyzed in FIFO order, and completed results are cached by position key for later reuse.
/// </summary>
public sealed class UciPositionAnalysisCoordinator : IDisposable, IAsyncDisposable
{
	private readonly Func<PositionAnalysisWorkItem, CancellationToken, Task<PositionAnalysisResult>> _analyzePositionAsync;
	private readonly Action? _beforeWorkerRetires;
	private readonly Action? _afterWorkDequeued;
	private readonly object _sync = new();
	private readonly Dictionary<string, string> _analysisContexts = new(StringComparer.Ordinal);
	private readonly Dictionary<string, long> _analysisRevisions = new(StringComparer.Ordinal);
	private long _nextAnalysisRevision;
	private readonly Queue<PositionAnalysisWorkItem> _pendingRequests = new();
	private readonly Dictionary<string, PositionAnalysisResult> _completedAnalyses = new(StringComparer.Ordinal);
	private readonly LinkedList<string> _completedOrder = new();
	private readonly int _maxCachedPositions;
	private readonly int _maxPendingPositions;
	private readonly Dictionary<string, TaskCompletionSource<PositionAnalysisResult>> _waiters =
		new(StringComparer.Ordinal);
	private CancellationTokenSource _lifetimeCts = new();
	private Task? _workerTask;
	private bool _disposed;
	private TaskCompletionSource<bool>? _disposalCancellationCompleted;
	private ExceptionDispatchInfo? _disposalFailure;

	internal Task? ActiveWorker
	{
		get
		{
			lock (_sync)
			{
				return _workerTask;
			}
		}
	}

	/// <summary>
	///     Initializes a new coordinator over a dedicated analysis client.
	/// </summary>
	public UciPositionAnalysisCoordinator(
		UciEngineClient client,
		int             multiPvMoveTimeMs  = 3_000,
		int             fallbackMoveTimeMs = 250,
		int             maxCachedPositions = 128,
		int             maxPendingPositions = 128)
		: this(CreateAnalyzer(client, multiPvMoveTimeMs, fallbackMoveTimeMs),
			maxCachedPositions: maxCachedPositions, maxPendingPositions: maxPendingPositions) { }

	internal UciPositionAnalysisCoordinator(
		Func<PositionAnalysisWorkItem, CancellationToken, Task<PositionAnalysisResult>> analyzePositionAsync,
		Action? beforeWorkerRetires = null,
		Action? afterWorkDequeued = null,
		int maxCachedPositions = 128,
		int maxPendingPositions = 128)
	{
		if (maxCachedPositions <= 0) throw new ArgumentOutOfRangeException(nameof(maxCachedPositions));
		if (maxPendingPositions <= 0) throw new ArgumentOutOfRangeException(nameof(maxPendingPositions));
		_maxCachedPositions = maxCachedPositions;
		_maxPendingPositions = maxPendingPositions;
		_analyzePositionAsync = analyzePositionAsync ?? throw new ArgumentNullException(nameof(analyzePositionAsync));
		_beforeWorkerRetires = beforeWorkerRetires;
		_afterWorkDequeued = afterWorkDequeued;
	}

	/// <summary>
	///     Queues the supplied position for ordered analysis when it has not already been completed or queued.
	/// </summary>
	public void Enqueue(
		string                 positionKey,
		IReadOnlyList<string>  moves,
		char                   sideToMove,
		char                   playerColor,
		ImmutableArray<string> legalMoves)
		=> Enqueue(positionKey, Fen.Default, moves, sideToMove, playerColor, legalMoves);

	/// <summary>
	///     Queues analysis with an explicit base position and its complete move history.
	/// </summary>
	public void Enqueue(
		string positionKey,
		Fen baseFen,
		IReadOnlyList<string> moves,
		char sideToMove,
		char playerColor,
		ImmutableArray<string> legalMoves)
	{
		if (string.IsNullOrWhiteSpace(positionKey))
			throw new ArgumentException("A position key is required.", nameof(positionKey));
		if (string.IsNullOrWhiteSpace(baseFen.Raw))
			throw new ArgumentException("A parsed base FEN is required.", nameof(baseFen));
		if (moves is null)
			throw new ArgumentNullException(nameof(moves));
		if (sideToMove is not ('w' or 'b'))
			throw new ArgumentOutOfRangeException(nameof(sideToMove));
		if (playerColor is not ('w' or 'b'))
			throw new ArgumentOutOfRangeException(nameof(playerColor));
		if (legalMoves.IsDefault)
			legalMoves = [];
		ImmutableArray<string> copiedMoves = [.. moves];
		string context = baseFen.Raw + "|" + sideToMove + playerColor + "|" +
			string.Join(" ", copiedMoves) + "|" + string.Join(" ", legalMoves);
		lock (_sync)
		{
			ThrowIfDisposed();
			if (_analysisContexts.TryGetValue(positionKey, out string? priorContext) && priorContext == context &&
				(_completedAnalyses.ContainsKey(positionKey) || _waiters.ContainsKey(positionKey)))
				return;

			if (!_waiters.ContainsKey(positionKey) && _waiters.Count >= _maxPendingPositions)
				throw new InvalidOperationException("The position-analysis queue has reached its configured pending-position limit.");

			if (_waiters.TryGetValue(positionKey, out var priorWaiter))
				priorWaiter.TrySetCanceled();
			_completedAnalyses.Remove(positionKey);
			_completedOrder.Remove(positionKey);
			// Replacing the same key must also release its obsolete queued history immediately.
			for (int remaining = _pendingRequests.Count; remaining > 0; remaining--)
			{
				var pending = _pendingRequests.Dequeue();
				if (pending.PositionKey != positionKey) _pendingRequests.Enqueue(pending);
			}
			_analysisContexts[positionKey] = context;
			long revision = ++_nextAnalysisRevision;
			_analysisRevisions[positionKey] = revision;
			_pendingRequests.Enqueue(
				new(
					positionKey,
					copiedMoves,
					sideToMove,
					playerColor,
					legalMoves,
					baseFen,
					revision
				)
			);

			_waiters[positionKey] = new(TaskCreationOptions.RunContinuationsAsynchronously);

			if (_workerTask is null || _workerTask.IsCompleted)
				_workerTask = Task.Run(ProcessLoopAsync);
		}
	}

	/// <summary>
	///     Attempts to read a completed analysis for the supplied position.
	/// </summary>
	public bool TryGetAnalysis(string positionKey, out PositionAnalysisResult analysis)
	{
		lock (_sync)
		{
			ThrowIfDisposed();
			if (_completedAnalyses.TryGetValue(positionKey, out analysis))
				return true;
		}

		analysis = default;
		return false;
	}

	/// <summary>
	///     Awaits completion of the supplied position when it has been queued for analysis.
	/// </summary>
	/// <exception cref="InvalidOperationException">Thrown when the coordinator is not tracking the supplied position.</exception>
	public Task<PositionAnalysisResult> GetAnalysisAsync(string positionKey)
	{
		lock (_sync)
		{
			ThrowIfDisposed();
			if (_completedAnalyses.TryGetValue(positionKey, out var analysis))
				return Task.FromResult(analysis);

			if (_waiters.TryGetValue(positionKey, out var waiter))
				return waiter.Task;
		}

		throw new InvalidOperationException("Position analysis is not available for the supplied position.");
	}

	/// <summary>
	///     Awaits a queued analysis, canceling only this caller's wait when requested. Shared analysis continues.
	/// </summary>
	public Task<PositionAnalysisResult> GetAnalysisAsync(string positionKey, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		var task = GetAnalysisAsync(positionKey);
		return !ct.CanBeCanceled || task.IsCompleted ? task : WaitWithCancellationAsync(task, ct);
	}

	private static async Task<PositionAnalysisResult> WaitWithCancellationAsync(
		Task<PositionAnalysisResult> task, CancellationToken ct)
	{
#if NET9_0_OR_GREATER
		return await task.WaitAsync(ct).ConfigureAwait(false);
#else
		var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = ct.Register(
			static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancellation);
		if (task != await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false))
			throw new OperationCanceledException(ct);
		return await task.ConfigureAwait(false);
#endif
	}

	/// <summary>
	///     Cancels in-flight work and clears pending and completed state.
	/// </summary>
	public void Cancel() => CancelCore(null);

	internal void CancelPendingAndRetainCompleted(ISet<string> retainedPositionKeys)
	{
		if (retainedPositionKeys is null)
			throw new ArgumentNullException(nameof(retainedPositionKeys));

		CancelCore(retainedPositionKeys);
	}

	private async Task ProcessLoopAsync()
	{
		while (true)
		{
			PositionAnalysisWorkItem request;
			CancellationTokenSource? generation;
			CancellationToken token;

			lock (_sync)
			{
				if (_pendingRequests.TryDequeue(out request))
				{
					if (!_analysisRevisions.TryGetValue(request.PositionKey, out long revision) || revision != request.ContextRevision)
						continue;
					generation = _lifetimeCts;
					token = generation.Token;
				}
				else
				{
					_workerTask = null;
					generation = null;
					token = default;
				}
			}

			if (generation is null)
			{
				_beforeWorkerRetires?.Invoke();
				return;
			}

			_afterWorkDequeued?.Invoke();

			try
			{
				var analysis = await _analyzePositionAsync(request, token).ConfigureAwait(false);

				TaskCompletionSource<PositionAnalysisResult>? waiter = null;
				lock (_sync)
				{
					if (!ReferenceEquals(generation, _lifetimeCts) ||
						!_analysisRevisions.TryGetValue(request.PositionKey, out long revision) || revision != request.ContextRevision)
						continue;

					_completedAnalyses[request.PositionKey] = analysis;
					_completedOrder.AddLast(request.PositionKey);
					while (_completedAnalyses.Count > _maxCachedPositions)
					{
						string expiredKey = _completedOrder.First!.Value;
						_completedOrder.RemoveFirst();
						_completedAnalyses.Remove(expiredKey);
						_analysisContexts.Remove(expiredKey);
						_analysisRevisions.Remove(expiredKey);
					}
					if (_waiters.TryGetValue(request.PositionKey, out waiter))
						_waiters.Remove(request.PositionKey);
				}

				waiter?.TrySetResult(analysis);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				continue;
			}
			catch (Exception ex)
			{
				TaskCompletionSource<PositionAnalysisResult>? waiter = null;
				lock (_sync)
				{
					if (!ReferenceEquals(generation, _lifetimeCts) ||
						!_analysisRevisions.TryGetValue(request.PositionKey, out long revision) || revision != request.ContextRevision)
						continue;

					if (_waiters.TryGetValue(request.PositionKey, out waiter))
						_waiters.Remove(request.PositionKey);
					_analysisContexts.Remove(request.PositionKey);
					_analysisRevisions.Remove(request.PositionKey);
				}

				waiter?.TrySetException(ex);
			}
		}
	}

	/// <summary>
	///     Cancels queued work and releases coordinator resources.
	/// </summary>
	public void Dispose() => CancelCore(null, dispose: true);

	/// <summary>Cancels analysis and observes the active worker before the caller releases its engine client.</summary>
	public async ValueTask DisposeAsync()
	{
		ExceptionDispatchInfo? cancellationFailure = null;
		try { Dispose(); }
		catch (Exception error) { cancellationFailure = ExceptionDispatchInfo.Capture(error); }
		Task? worker;
		Task cancellationCompleted;
		lock (_sync)
		{
			worker = _workerTask;
			cancellationCompleted = _disposalCancellationCompleted?.Task ?? Task.CompletedTask;
		}
		await cancellationCompleted.ConfigureAwait(false);
		lock (_sync) cancellationFailure = _disposalFailure ?? cancellationFailure;
		try
		{
			if (worker is not null) await worker.ConfigureAwait(false);
		}
		catch (Exception workerFailure) when (cancellationFailure is not null)
		{
			throw new AggregateException("Analysis cancellation and worker shutdown both failed.",
				cancellationFailure.SourceException, workerFailure);
		}
		cancellationFailure?.Throw();
	}

	private void CancelCore(ISet<string>? retainedPositionKeys, bool dispose = false) =>
		BeginCancellation(retainedPositionKeys, dispose)?.Invoke();

	// State invalidation happens immediately. The caller invokes the returned cleanup once, outside its own gates.
	internal Action? BeginCancellation(ISet<string>? retainedPositionKeys = null, bool dispose = false)
	{
		CancellationTokenSource retiredGeneration;
		TaskCompletionSource<PositionAnalysisResult>[] waiters;
		TaskCompletionSource<bool>? disposalCompletion = null;

		lock (_sync)
		{
			if (_disposed)
			{
				if (dispose) return null;
				ThrowIfDisposed();
			}
			if (dispose) disposalCompletion = _disposalCancellationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
			_disposed = dispose;
			retiredGeneration = _lifetimeCts;
			if (!dispose) _lifetimeCts = new();
			waiters = [.. _waiters.Values];
			_pendingRequests.Clear();
			_waiters.Clear();

			if (retainedPositionKeys is null)
			{
				_completedAnalyses.Clear();
				_completedOrder.Clear();
			}
			else
			{
				foreach (var positionKey in _completedAnalyses.Keys.ToArray())
				{
					if (!retainedPositionKeys.Contains(positionKey))
					{
						_completedAnalyses.Remove(positionKey);
						_completedOrder.Remove(positionKey);
					}
				}
			}
			foreach (string key in _analysisContexts.Keys.ToArray())
			{
				if (!_completedAnalyses.ContainsKey(key))
				{
					_analysisContexts.Remove(key);
					_analysisRevisions.Remove(key);
				}
			}
		}

		return () =>
		{
			// Promise completion cannot run continuations inline. Publish cancellation before callbacks,
			// so a callback can observe a retired wait without depending on its own return.
			foreach (var waiter in waiters) waiter.TrySetCanceled();
			try
			{
				try { retiredGeneration.Cancel(); }
				finally { retiredGeneration.Dispose(); }
			}
			catch (Exception error)
			{
				if (disposalCompletion is not null)
					lock (_sync) _disposalFailure = ExceptionDispatchInfo.Capture(error);
				throw;
			}
			finally { disposalCompletion?.TrySetResult(true); }
		};
	}

	private void ThrowIfDisposed()
	{
		if (_disposed) throw new ObjectDisposedException(nameof(UciPositionAnalysisCoordinator));
	}

	private static Func<PositionAnalysisWorkItem, CancellationToken, Task<PositionAnalysisResult>> CreateAnalyzer(
		UciEngineClient client,
		int multiPvMoveTimeMs,
		int fallbackMoveTimeMs)
	{
		if (client is null) throw new ArgumentNullException(nameof(client));
		if (multiPvMoveTimeMs <= 0) throw new ArgumentOutOfRangeException(nameof(multiPvMoveTimeMs));
		if (fallbackMoveTimeMs <= 0) throw new ArgumentOutOfRangeException(nameof(fallbackMoveTimeMs));

		return async (workItem, token) =>
		{
			if (client.TryGetOption("UCI_LimitStrength", out var strength) && strength.Type == UciOptionType.Check)
				await client.SetOptionAsync(strength.Name, "false", token).ConfigureAwait(false);
			if (client.TryGetOption("Skill Level", out var skill) && skill.Type == UciOptionType.Spin && skill.Max is int maxSkill)
				await client.SetOptionAsync(skill.Name, maxSkill.ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
			if (client.TryGetOption("UCI_ShowWDL", out var showWdl) && showWdl.Type == UciOptionType.Check)
				await client.SetOptionAsync(showWdl.Name, "true", token).ConfigureAwait(false);
			await client.SetPositionAsync(workItem.BaseFen, workItem.Moves, token).ConfigureAwait(false);
			return await client.AnalyzePositionAsync(
				workItem.SideToMove,
				workItem.PlayerColor,
				workItem.LegalMoves,
				multiPvMoveTimeMs,
				fallbackMoveTimeMs,
				token
			).ConfigureAwait(false);
		};
	}
}

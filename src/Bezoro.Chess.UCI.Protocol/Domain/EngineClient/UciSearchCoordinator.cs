using System.Threading;
using System.Threading.Tasks;
using Bezoro.Chess.UCI.Protocol.Domain.Common.Constants;

namespace Bezoro.Chess.UCI.Protocol.Domain.EngineClient;

/// <summary>
///     Coordinates UCI search sessions, handling lifecycle, captured output lines, and bestmove completion.
/// </summary>
internal sealed class UciSearchCoordinator(
	IUciTransport               transport,
	Action<EngineActivity>      setActivity,
	UciClientOptions            options
)
{
	private readonly Action<EngineActivity> _setActivity =
		setActivity ?? throw new ArgumentNullException(nameof(setActivity));
	private readonly UciClientOptions _options = options;

	private readonly IUciTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));

	private volatile SearchSession? _activeSession;
	private int _requiresRestart;
	private readonly SemaphoreSlim _commandGate = new(1, 1);

	private TimeSpan StopGracePeriod => _options.SearchStopGracePeriod > TimeSpan.Zero
		? _options.SearchStopGracePeriod
		: TimeSpan.FromSeconds(2);

	public Task<SearchResult> ExecuteSearchAsync(SearchParameters parameters, CancellationToken ct) =>
		ExecuteSearchInternalAsync(parameters, ct);

	public void HandleBestMove(UciBestMoveMessage message)
	{
		var session = _activeSession;
		_setActivity(EngineActivity.Idle);
		if (session is null) return;
		session.CompleteBestMove(message);
		if (session.IsFireAndForget)
		{
			Interlocked.CompareExchange(ref _activeSession, null, session);
			session.CleanupCompletion.TrySetResult(null);
		}
	}

	public async Task ExecuteFireAndForgetAsync(SearchParameters parameters, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		EnsureSynchronized();
		string command = UciCommandBuilder.BuildGoCommand(parameters);
		var session = new SearchSession(parameters.Ponder, fireAndForget: true);
		if (Interlocked.CompareExchange(ref _activeSession, session, null) is not null)
			throw new InvalidOperationException("A search is already in progress.");

		try
		{
			await WriteSearchCommandAsync(session, command, ct).ConfigureAwait(false);
		}
		catch
		{
			CompleteSessionCleanup(session);
			throw;
		}
	}

	public async Task StopSearchAsync(CancellationToken ct)
	{
		SearchSession? session;
		await _commandGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			session = _activeSession;
			await _transport.WriteLineAsync(UciConstants.Commands.STOP, ct).ConfigureAwait(false);
			if (session is null) _setActivity(EngineActivity.Idle);
		}
		finally { _commandGate.Release(); }
		if (session is null) return;

		try
		{
			await AwaitBestMoveGracefullyAsync(session, session.BestMoveCompletion.Task, CancellationToken.None).ConfigureAwait(false);
			// A regular search may still be sending its own stop after cancellation or timeout.
			// Keep its slot reserved until that path has finished, rather than only awaiting bestmove.
			await session.CleanupCompletion.Task.ConfigureAwait(false);
			ct.ThrowIfCancellationRequested();
		}
		catch (TimeoutException)
		{
			Interlocked.Exchange(ref _requiresRestart, 1);
			throw;
		}
		finally
		{
			if (session.IsFireAndForget && !session.CleanupCompletion.Task.IsCompleted)
				CompleteSessionCleanup(session);
		}
	}

	private async Task WriteSearchCommandAsync(SearchSession session, string command, CancellationToken ct)
	{
		await _commandGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			_setActivity(session.Ponder ? EngineActivity.Pondering : EngineActivity.Searching);
			await _transport.WriteLineAsync(command, ct).ConfigureAwait(false);
		}
		finally { _commandGate.Release(); }
	}

	private void EnsureSynchronized()
	{
		if (Volatile.Read(ref _requiresRestart) != 0)
			throw new InvalidOperationException("The engine did not acknowledge stop with bestmove. Stop and restart the client before searching again.");
	}

	public void HandleInfo(UciInfoPayload info) => _activeSession?.RecordInfo(info);

	public void HandleTransportTerminated()
	{
		Interlocked.Exchange(ref _requiresRestart, 0);
		var session = Interlocked.Exchange(ref _activeSession, null);
		session?.BestMoveCompletion.TrySetCanceled();
		if (session is { IsFireAndForget: true })
			session.CleanupCompletion.TrySetResult(null);
	}

	private void CompleteSessionCleanup(SearchSession session)
	{
		try
		{
			if (ReferenceEquals(_activeSession, session))
				_setActivity(EngineActivity.Idle);
		}
		finally
		{
			Interlocked.CompareExchange(ref _activeSession, null, session);
			session.BestMoveCompletion.TrySetCanceled();
			session.CleanupCompletion.TrySetResult(null);
		}
	}

	private TimeSpan ComputeTimeout(SearchParameters parameters)
	{
		if (parameters.MoveTimeMs is { } mt)
		{
			double buffered = mt + _options.MoveTimeBuffer.TotalMilliseconds;
			double minimum  = _options.MinimumMoveTimeTimeout.TotalMilliseconds;
			if (buffered < minimum) buffered = minimum;

			double capped = Math.Min(buffered, TimeSpan.MaxValue.TotalMilliseconds);
			return TimeSpan.FromMilliseconds(capped);
		}

		if (parameters.Infinite) return _options.InfiniteSearchTimeout;
		if (parameters.Depth is not { } depth)
			return parameters.Nodes is { }
					   ? _options.NodeSearchTimeout
					   : _options.DefaultSearchTimeout;

		long seconds = Math.Clamp(
			depth * (long)_options.DepthTimeoutSecondsPerPly,
			(long)_options.MinimumDepthSearchTimeout.TotalSeconds,
			(long)_options.MaximumDepthSearchTimeout.TotalSeconds
		);
		return TimeSpan.FromSeconds(seconds);
	}

	private async Task DrainSearchAfterStopAsync(SearchSession session, Task<UciBestMoveMessage> bestTask)
	{
		try
		{
			_ = await AwaitBestMoveGracefullyAsync(session, bestTask, CancellationToken.None).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			// UCI has no request IDs. A delayed bestmove must never satisfy the next search.
			Interlocked.Exchange(ref _requiresRestart, 1);
		}
	}

	private async Task IssueStopBestEffortAsync()
	{
		try
		{
			using var stopCts = new CancellationTokenSource(StopGracePeriod);
			await _transport.WriteLineAsync(UciConstants.Commands.STOP, stopCts.Token).ConfigureAwait(false);
		}
		catch
		{
			/* best-effort */
		}
	}

	private async Task<SearchResult> ExecuteSearchInternalAsync(SearchParameters parameters, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		EnsureSynchronized();

		var timeout = ComputeTimeout(parameters);
		if (timeout != Timeout.InfiniteTimeSpan)
		{
			if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(parameters), "Search timeout must be nonnegative or infinite.");
			// Unity runtimes implement the portable Task.Delay limit with a signed millisecond count.
			timeout = TimeSpan.FromMilliseconds(Math.Min(timeout.TotalMilliseconds, int.MaxValue));
		}
		string cmd     = UciCommandBuilder.BuildGoCommand(parameters);
		var    session = new SearchSession(parameters.Ponder, maxCapturedVariations: _options.MaxCapturedPrincipalVariations);

		if (Interlocked.CompareExchange(ref _activeSession, session, null) is { })
		{
			session.BestMoveCompletion.TrySetCanceled();
			throw new InvalidOperationException("A search is already in progress.");
		}

		using var timerCts = new CancellationTokenSource();
		UciBestMoveMessage? bestMove = null;
		var     callerCanceled = false;

		try
		{
			await WriteSearchCommandAsync(session, cmd, ct).ConfigureAwait(false);

			var bestTask = session.BestMoveCompletion.Task;
			var timeoutTask = timeout == Timeout.InfiniteTimeSpan
								  ? Task.Delay(Timeout.Infinite, timerCts.Token)
								  : Task.Delay(timeout, timerCts.Token);

			var       cancelTcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
			using var reg = ct.Register(static s => ((TaskCompletionSource<object?>)s!).TrySetResult(null), cancelTcs);

			var completed = await Task.WhenAny(bestTask, timeoutTask, cancelTcs.Task).ConfigureAwait(false);

			if (completed == cancelTcs.Task)
			{
				callerCanceled = true;
				await IssueStopBestEffortAsync().ConfigureAwait(false);
				await DrainSearchAfterStopAsync(session, bestTask).ConfigureAwait(false);
			}

			else if (completed == bestTask)
			{
				bestMove = await bestTask.ConfigureAwait(false);
			}
			else
			{
				await IssueStopBestEffortAsync().ConfigureAwait(false);
				try
				{
					bestMove = await AwaitBestMoveGracefullyAsync(session, bestTask, CancellationToken.None).ConfigureAwait(false);
					callerCanceled = ct.IsCancellationRequested;
				}
				catch (TimeoutException)
				{
					bestMove = null;
					callerCanceled = ct.IsCancellationRequested;
					Interlocked.Exchange(ref _requiresRestart, 1);
				}
			}
		}
		finally
		{
			timerCts.Cancel();
			CompleteSessionCleanup(session);
		}

		if (callerCanceled)
			throw new OperationCanceledException(ct);

		if (bestMove is { } resolvedBestMove)
			return session.BuildResult(resolvedBestMove);

		throw new InvalidOperationException(
			"Engine search completed with malformed output: missing or invalid 'bestmove' line."
		);
	}

	private async Task<UciBestMoveMessage?> AwaitBestMoveGracefullyAsync(
		SearchSession     session,
		Task<UciBestMoveMessage> bestTask,
		CancellationToken ct)
	{
		try
		{
			var delay = StopGracePeriod;
			using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			var grace = await Task.WhenAny(bestTask, Task.Delay(delay, graceCts.Token)).ConfigureAwait(false);
			graceCts.Cancel();

			ct.ThrowIfCancellationRequested();

			if (grace == bestTask)
				return await bestTask.ConfigureAwait(false);

			UciBestMoveMessage? captured = session.BestMove;
			if (!captured.HasValue)
				throw new TimeoutException("Engine search timed out without emitting 'bestmove'.");

			return captured;
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			UciBestMoveMessage? captured = session.BestMove;
			if (!captured.HasValue) throw;

			return captured;
		}
	}
}

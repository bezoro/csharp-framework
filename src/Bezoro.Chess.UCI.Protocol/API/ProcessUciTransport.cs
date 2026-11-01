using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;
using Bezoro.Logging;
using Bezoro.Logging.Types;

namespace Bezoro.Chess.UCI.Protocol.API;

/// <summary>
///     Provides a process-backed UCI transport that owns engine startup, async line I/O, and teardown behavior.
/// </summary>
public sealed class ProcessUciTransport : IUciTransport
{
	private readonly BackgroundLoopManager      _loopManager;
	private readonly BackgroundLoopMetrics      _metrics;
	private readonly GateManager                _gateManager;
	private readonly IReadOnlyList<string>?     _args;
	private readonly ProcessStreams             _streams;
	private readonly ProcessUciTransportOptions _options;
	private readonly string                     _path;
	private readonly string?                    _workingDirectory;
	private readonly TransportStateManager      _stateManager;

	private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

	private Channel<string>? _lines;
	private Channel<string>? _outgoing;
	private Process?         _process;

	/// <summary>
	///     Raised when an internal transport error is observed.
	/// </summary>
	public event Action<Exception>? Error;

	/// <summary>
	///     Raised when the engine process exits.
	/// </summary>
	public event Action<int?, string?>? Exited;

	/// <summary>
	///     Raised for lines received from redirected stderr.
	/// </summary>
	public event Action<string>? StderrReceived;

	/// <summary>
	///     Initializes a new process-backed transport with default transport options.
	/// </summary>
	/// <param name="path">Path to the engine executable.</param>
	/// <param name="args">Optional process arguments passed to the engine.</param>
	/// <param name="workingDirectory">Optional working directory used when starting the engine.</param>
	public ProcessUciTransport(string path, IEnumerable<string>? args = null, string? workingDirectory = null)
		: this(path, args, workingDirectory, null) { }

	/// <summary>
	///     Initializes a new process-backed transport with explicit options.
	/// </summary>
	/// <param name="path">Path to the engine executable.</param>
	/// <param name="args">Optional process arguments passed to the engine.</param>
	/// <param name="workingDirectory">Optional working directory used when starting the engine.</param>
	/// <param name="options">Transport behavior overrides; when <see langword="null" />, defaults are used.</param>
	public ProcessUciTransport(
		string                      path,
		IEnumerable<string>?        args,
		string?                     workingDirectory,
		ProcessUciTransportOptions? options)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new ArgumentException("Engine path must be provided.", nameof(path));

		_path             = path;
		_workingDirectory = workingDirectory;
		_args             = args is null ? null : ValidateAndCreateArgsList(args);
		_options          = options ?? new ProcessUciTransportOptions();

		ProcessUciTransportValidator.ValidateOptions(_options);

		_metrics      = new();
		_stateManager = new();
		_gateManager  = new();
		_streams      = new();
		_loopManager = new(
			_options,
			ReportError,
			_metrics,
			line => StderrReceived?.Invoke(line)
		);
	}

	/// <summary>
	///     Gets a best-effort signal indicating whether the process and background loops are still healthy.
	/// </summary>
	public bool IsHealthy => IsStarted && ProcessIsAlive() && _loopManager.AreLoopsHealthy();

	/// <summary>
	///     Gets a value indicating whether the transport has been started and not yet fully stopped or disposed.
	/// </summary>
	public bool IsStarted => _stateManager.IsStarted;

	/// <summary>
	///     Gets the number of times a write encountered channel backpressure.
	/// </summary>
	public long BackpressureEvents => _metrics.BackpressureEvents;

	/// <summary>
	///     Gets the number of stdout lines read from the engine.
	/// </summary>
	public long LinesRead => _metrics.LinesRead;

	/// <summary>
	///     Gets the number of command lines written to the engine.
	/// </summary>
	public long LinesWritten => _metrics.LinesWritten;

	/// <summary>
	///     Gets the current lifecycle state for the transport.
	/// </summary>
	public TransportStatus Status => _stateManager.Status;

	/// <summary>
	///     Asynchronously reads lines from the UCI engine's stdout.
	/// </summary>
	/// <param name="ct">Cancellation token to stop reading.</param>
	/// <returns>An async enumerable of lines read from the engine.</returns>
	/// <exception cref="InvalidOperationException">Thrown if transport is not started or is disposed.</exception>
	/// <remarks>
	///     If SingleReader option is true, only one concurrent reader is allowed.
	///     The enumerable will complete when the transport is stopped or the process exits.
	/// </remarks>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct = default)
	{
		_stateManager.ThrowIfDisposed(nameof(ProcessUciTransport));

		var reader = _lines?.Reader ?? throw new InvalidOperationException("Transport not started.");

		_stateManager.EnsureSingleReader(_options.SingleReader);

		try
		{
			await foreach (string line in reader.ReadAllAsync(ct).ConfigureAwait(false))
				yield return line;
		}
		finally
		{
			_stateManager.ReleaseReaderIfSingle(_options.SingleReader);
		}
	}

	/// <summary>
	///     Starts the UCI engine process and initializes the transport.
	/// </summary>
	/// <param name="ct">Cancellation token to cancel the start operation.</param>
	/// <exception cref="InvalidOperationException">Thrown if the process fails to start or file system validation fails.</exception>
	/// <exception cref="OperationCanceledException">Thrown if the cancellation token is triggered.</exception>
	/// <remarks>
	///     This method is idempotent - calling it multiple times concurrently will result in only one start operation.
	///     If the transport is already started and alive, this method returns immediately.
	///     Thread-safe: Multiple concurrent calls are serialized via internal gating.
	/// </remarks>
	public async Task StartAsync(CancellationToken ct = default)
	{
		await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
		try { await StartCoreAsync(ct).ConfigureAwait(false); }
		finally { _lifecycleGate.Release(); }
	}

	private async Task StartCoreAsync(CancellationToken ct)
	{
		_stateManager.ThrowIfDisposed(nameof(ProcessUciTransport));

		if (IsStartedAndAlive()) return;

		var existingStart = _gateManager.GetStartingSignal();
		if (existingStart is { })
		{
			await GateManager.AwaitExistingStartAsync(existingStart, ct).ConfigureAwait(false);
			return;
		}

		_stateManager.EnsureStartable();

		bool acquired = await _gateManager.AcquireStartGateAsync(ct).ConfigureAwait(false);
		if (!acquired) return;

		var startingSignal = _gateManager.PublishStartingSignal();

		try
		{
			PrepareForStart();
			ct.ThrowIfCancellationRequested();

			string workingDir = ResolveWorkingDirectory();
			ProcessUciTransportValidator.ValidateFileSystem(_path, workingDir, _stateManager);

			CleanupExitedProcess();

			await CreateProcessAndRunPipelinesAsync(workingDir, startingSignal, ct).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			SignalStartFailure(startingSignal, ex, ct);
			await CleanupAfterFailedStartSafeAsync().ConfigureAwait(false);
			_stateManager.ResetStatusIfNeeded();
			Logger.Log(ex, category: LogCategory.Uci);
			throw;
		}
		finally
		{
			_gateManager.ReleaseStartGate();
		}
	}

	/// <summary>
	///     Gracefully stops the UCI engine process and cleans up resources.
	/// </summary>
	/// <param name="ct">Cancellation token to cancel the stop operation.</param>
	/// <exception cref="OperationCanceledException">Thrown if the cancellation token is triggered.</exception>
	/// <remarks>
	///     Sends "quit" command to the engine if SendQuitOnStop option is true.
	///     Waits for graceful process exit before forcefully terminating if necessary.
	///     Thread-safe: Multiple concurrent calls are serialized via internal gating.
	///     If transport is not started, this method completes immediately with no operation.
	/// </remarks>
	public async Task StopAsync(CancellationToken ct = default)
	{
		await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
		try { await StopCoreAsync(ct).ConfigureAwait(false); }
		finally { _lifecycleGate.Release(); }
	}

	private async Task StopCoreAsync(CancellationToken ct)
	{
		_stateManager.ThrowIfDisposed(nameof(ProcessUciTransport));
		ct.ThrowIfCancellationRequested();

		var existingStart = _gateManager.GetStartingSignal();
		if (existingStart != null)
			await GateManager.AwaitExistingStartAsync(existingStart, ct).ConfigureAwait(false);

		if (!_stateManager.IsStarted && _process is null)
		{
			_stateManager.SetStatus(TransportStatus.Stopped);
			Logger.Log("StopAsync: transport not started; no-op.", category: LogCategory.Uci);
			return;
		}

		if (await _gateManager.AwaitExistingStopIfAnyAsync(ct).ConfigureAwait(false)) return;

		var stopTcs = _gateManager.PublishStoppingSignal();

		try
		{
			_stateManager.SetStatus(TransportStatus.Stopping);
			Logger.Log("Stopping UCI transport.", category: LogCategory.Uci);

			await TearDownCoreAsync(_options.SendQuitOnStop, TransportStatus.Stopped, "Stopped UCI transport.")
				.ConfigureAwait(false);

			stopTcs.TrySetResult(null);
		}
		catch (Exception ex)
		{
			try
			{
				stopTcs.TrySetException(ex);
			}
			catch
			{
				// Suppress: TrySetException can throw if TCS is already completed (race condition)
				// The original exception is being re-thrown, so this is non-critical
			}

			throw;
		}
		finally
		{
			_gateManager.ReleaseStopGate();
		}
	}

	/// <summary>
	///     Asynchronously writes a command line to the UCI engine's stdin.
	/// </summary>
	/// <param name="line">The command line to send to the engine.</param>
	/// <param name="ct">Cancellation token to cancel the write operation.</param>
	/// <exception cref="ArgumentNullException">Thrown if line is null.</exception>
	/// <exception cref="InvalidOperationException">Thrown if transport is not started, disposed, or process is not alive.</exception>
	/// <exception cref="OperationCanceledException">Thrown if the cancellation token is triggered.</exception>
	/// <remarks>
	///     This method will wait indefinitely if the channel is full (backpressure).
	///     A warning is logged if the write is blocked for longer than
	///     <see cref="ProcessUciTransportOptions.WriteHangWarningThreshold" />.
	///     Use TryWriteLineAsync with a timeout if you need bounded wait time.
	///     Command validation is performed if ValidateCommands option is true.
	/// </remarks>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public async Task WriteLineAsync(string line, CancellationToken ct = default)
	{
		ValidateWritePreconditions(line, ct);

		var writer = GetOutgoingWriterOrThrow();

		if (writer.TryWrite(line)) return;

		_metrics.IncrementBackpressureEvents();

		// Set up warning if write hangs for too long
		var warningThreshold = _options.WriteHangWarningThreshold;
		if (warningThreshold != Timeout.InfiniteTimeSpan && warningThreshold > TimeSpan.Zero)
			await WriteWithHangWarningAsync(writer, line, warningThreshold, ct).ConfigureAwait(false);
		else
			// No warning configured - write directly
			try
			{
				await writer.WriteAsync(line, ct).ConfigureAwait(false);
			}
			catch (ChannelClosedException)
			{
				throw new InvalidOperationException("Transport is stopping or stopped; cannot write.");
			}
	}

	/// <summary>
	///     Attempts to write a command line to the UCI engine's stdin with a timeout.
	/// </summary>
	/// <param name="line">The command line to send to the engine.</param>
	/// <param name="timeout">
	///     Maximum time to wait for the write operation. Use TimeSpan.Zero for immediate return, or
	///     Timeout.InfiniteTimeSpan for no timeout.
	/// </param>
	/// <param name="ct">Cancellation token to cancel the write operation.</param>
	/// <returns>True if the line was successfully written within the timeout; false if the timeout expired.</returns>
	/// <exception cref="ArgumentNullException">Thrown if line is null.</exception>
	/// <exception cref="ArgumentOutOfRangeException">Thrown if timeout is negative (except Timeout.InfiniteTimeSpan).</exception>
	/// <exception cref="InvalidOperationException">Thrown if transport is not started, disposed, or process is not alive.</exception>
	/// <exception cref="OperationCanceledException">Thrown if the cancellation token is triggered (not timeout expiration).</exception>
	/// <remarks>
	///     For very small timeouts (≤1ms), uses busy-waiting to reduce context switching overhead.
	///     Command validation is performed if ValidateCommands option is true.
	/// </remarks>
	public async Task<bool> TryWriteLineAsync(string line, TimeSpan timeout, CancellationToken ct = default)
	{
		ValidateTryWritePreconditions(line, timeout);
		ct.ThrowIfCancellationRequested();

		if (_options.ValidateCommands) ProcessUciTransportValidator.ValidateCommandLine(line);

		var writer = GetOutgoingWriterOrThrow();

		if (writer.TryWrite(line)) return true;

		_metrics.IncrementBackpressureEvents();

		if (timeout == TimeSpan.Zero) return false;

		if (TryHandleSmallTimeout(writer, line, ct, timeout)) return true;

		try
		{
			return timeout == Timeout.InfiniteTimeSpan
					   ? await TryWriteWithInfiniteTimeoutAsync(writer, line, ct).ConfigureAwait(false)
					   : await TryWriteWithFiniteTimeoutAsync(writer, line, timeout, ct).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			return InterpretWriteException(ex, ct);
		}
	}

	/// <summary>
	///     Asynchronously disposes the transport and the underlying engine process.
	/// </summary>
	/// <returns>A task that completes when teardown has finished.</returns>
	public async ValueTask DisposeAsync()
	{
		await _lifecycleGate.WaitAsync().ConfigureAwait(false);
		try { await DisposeCoreAsync().ConfigureAwait(false); }
		finally { _lifecycleGate.Release(); }
	}

	private async ValueTask DisposeCoreAsync()
	{
		if (!_stateManager.TryMarkDisposed()) return;

		// If a stop operation is in progress, wait for it to complete first
		try
		{
			if (await _gateManager.AwaitExistingStopIfAnyAsync(default).ConfigureAwait(false))
			{
				// Stop already completed, just update status to Disposed
				_stateManager.SetStatus(TransportStatus.Disposed);
				Logger.Log("Disposed UCI transport (after stop).", category: LogCategory.Uci);
				_gateManager.ReleaseStopGate();
				return;
			}
		}
		catch
		{
			// Ignore exceptions from stop operation - we'll proceed with disposal
		}

		_stateManager.SetStatus(TransportStatus.Stopping);
		Logger.Log("Disposing UCI transport.", category: LogCategory.Uci);

		try
		{
			await TearDownCoreAsync(_options.SendQuitOnDispose, TransportStatus.Disposed, "Disposed UCI transport.")
				.ConfigureAwait(false);
		}
		finally
		{
			_gateManager.ReleaseStopGate();
		}
	}

	/// <summary>
	///     Synchronous dispose implementation. Prefer <see cref="DisposeAsync" /> for async scenarios.
	/// </summary>
	/// <remarks>
	///     WARNING: This synchronous dispose blocks the calling thread until all async cleanup operations complete.
	///     This can potentially cause deadlocks if called from a synchronization context (e.g., UI thread).
	///     Always prefer <see cref="DisposeAsync" /> when possible. This method is provided only for compatibility
	///     with code that requires synchronous IDisposable (e.g., using statements without await).
	/// </remarks>
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
	public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool InterpretWriteException(Exception ex, CancellationToken ct)
	{
		return ex switch
		{
			OperationCanceledException when ct.IsCancellationRequested => throw ex,
			OperationCanceledException                                 => false,
			ChannelClosedException => throw new InvalidOperationException(
										  "Transport is stopping or stopped; cannot write."
									  ),
			_ => throw ex
		};
	}

	/// <summary>
	///     Safely checks if a process has exited, handling race conditions.
	/// </summary>
	private static bool ProcessHasExitedSafe(Process? process)
	{
		if (process is null) return true;

		try
		{
			return process.HasExited;
		}
		catch (InvalidOperationException)
		{
			// Process was disposed between null check and property access
			return true;
		}
	}

	/// <summary>
	///     Validates and creates an immutable list of arguments, ensuring no null values.
	/// </summary>
	private static IReadOnlyList<string>? ValidateAndCreateArgsList(IEnumerable<string>? args)
	{
		if (args is null) return null;

		// Validate and materialize in a single pass to avoid double enumeration
		var list = new List<string>();
		foreach (string? arg in args)
		{
			if (arg is null)
				throw new ArgumentException("Argument list cannot contain null values.", nameof(args));

			list.Add(arg);
		}

		return list;
	}

	/// <summary>
	///     Writes to the channel with a warning if the operation is blocked for too long.
	/// </summary>
	private static async Task WriteWithHangWarningAsync(
		ChannelWriter<string> writer,
		string                line,
		TimeSpan              warningThreshold,
		CancellationToken     ct)
	{
		using var warningCts  = new CancellationTokenSource(warningThreshold);
		var       warningTask = Task.Delay(warningThreshold, warningCts.Token);
		var       writeTask   = writer.WriteAsync(line, ct).AsTask();

		var completed = await Task.WhenAny(writeTask, warningTask).ConfigureAwait(false);

		if (completed == warningTask)
			Logger.Log(
				$"WARNING: WriteLineAsync has been blocked for {warningThreshold.TotalSeconds:F1}s waiting for channel space. " +
				"The consumer may not be reading, or the channel is saturated. " +
				"Consider using TryWriteLineAsync with a timeout for better control.",
				category: LogCategory.Uci
			);
		else
			warningCts.Cancel();

		try
		{
			await writeTask.ConfigureAwait(false);
		}
		catch (ChannelClosedException)
		{
			throw new InvalidOperationException("Transport is stopping or stopped; cannot write.");
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static async Task<bool> TryWriteWithFiniteTimeoutAsync(
		ChannelWriter<string> writer,
		string                line,
		TimeSpan              timeout,
		CancellationToken     ct)
	{
		using var timeoutCts = new CancellationTokenSource(timeout);

		if (!ct.CanBeCanceled)
		{
			await writer.WriteAsync(line, timeoutCts.Token).ConfigureAwait(false);
		}
		else
		{
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
			await writer.WriteAsync(line, linked.Token).ConfigureAwait(false);
		}

		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static async Task<bool> TryWriteWithInfiniteTimeoutAsync(
		ChannelWriter<string> writer,
		string                line,
		CancellationToken     ct)
	{
		await WriteOperationHelper.WriteWithCallerCancellationAsync(writer, line, ct).ConfigureAwait(false);
		return true;
	}

	private bool IsStartedAndAlive() =>
		_stateManager.IsStarted && _process is { HasExited: false };

	private bool ProcessIsAlive() => _process is { HasExited: false };

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryHandleSmallTimeout(
		ChannelWriter<string> writer,
		string                line,
		CancellationToken     ct,
		TimeSpan              timeout)
	{
		if (!WriteOperationHelper.ShouldSpinForSmallTimeout(timeout)) return false;

		return WriteOperationHelper.SpinUntilWriteOrCancel(writer, line, ct, _options.SmallTimeoutSpinIterations);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private ChannelWriter<string> GetOutgoingWriterOrThrow()
	{
		if (_outgoing is null) throw new InvalidOperationException("Transport is not started.");

		return _outgoing.Writer;
	}

	private string ResolveWorkingDirectory() =>
		string.IsNullOrWhiteSpace(_workingDirectory) ? Environment.CurrentDirectory : _workingDirectory;

	/// <summary>
	///     Awaits completion of read loops and nulls the lines channel.
	/// </summary>
	private async Task AwaitReadLoopsCompletionAsync()
	{
		try
		{
			await _loopManager.AwaitReadLoopAsync().ConfigureAwait(false);
			await _loopManager.AwaitStderrLoopAsync().ConfigureAwait(false);
			_lines = null;
		}
		catch (Exception ex)
		{
			ReportError(ex, "Failed to await read loops completion.");
		}
	}

	/// <summary>
	///     Cleans up resources after a failed start attempt.
	///     This includes canceling loops, disposing streams, and killing the process if necessary.
	/// </summary>
	private async Task CleanupAfterFailedStartAsync()
	{
		_stateManager.MarkProcessAlive(false);

		// For failed start, we can dispose everything immediately - no need to drain output
		CancelReadLoopAndDisposeStreams();
		var p = Interlocked.Exchange(ref _process, null);
		await Task.WhenAll(CleanupWriteLoopAndStdinAsync(false), CleanupProcessAsync(p, true)).ConfigureAwait(false);
		await AwaitReadLoopsCompletionAsync().ConfigureAwait(false);

		_stateManager.ResetStatusIfNeeded();
	}

	private async Task CleanupAfterFailedStartSafeAsync()
	{
		try
		{
			await CleanupAfterFailedStartAsync().ConfigureAwait(false);
		}
		catch (Exception cleanupEx)
		{
			ReportError(cleanupEx, "Cleanup after failed start threw.");
		}
	}

	/// <summary>
	///     Cleans up process, killing if necessary and waiting for exit.
	/// </summary>
	private async Task CleanupProcessAsync(Process? process, bool killIfNotExited)
	{
		if (process is null) return;

		List<Exception> errors = [];
		try
		{
			if (killIfNotExited)
				await KillProcessImmediatelyAsync(process, errors).ConfigureAwait(false);
			else
				await HandleGracefulShutdownAsync(process, errors).ConfigureAwait(false);

			try
			{
				await _loopManager.AwaitExitNotificationAsync(_options.TeardownTimeout).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				errors.Add(ex);
			}

			if (!ProcessHasExitedSafe(process))
				errors.Add(new InvalidOperationException("UCI engine process did not exit during teardown."));
		}
		finally
		{
			ProcessHelper.SafeDisposeProcess(process);
		}

		if (errors.Count == 1) throw errors[0];
		if (errors.Count > 1) throw new AggregateException("UCI transport teardown failed.", errors);
	}

	/// <summary>
	///     Cleans up write loop and stdin stream.
	/// </summary>
	private async Task CleanupWriteLoopAndStdinAsync(bool sendQuit)
	{
		// Send quit BEFORE canceling write loop if requested
		if (sendQuit && _streams.Stdin != null)
			try
			{
				await _streams.Stdin.WriteLineAsync("quit").ConfigureAwait(false);
				await _streams.Stdin.FlushAsync().ConfigureAwait(false);
				_options.OnQuitSent?.Invoke();
			}
			catch (Exception ex)
			{
				ReportError(ex, "Failed to write quit to stdin during cleanup.");
			}

		try
		{
			_loopManager.CancelWriteLoop();
			ChannelFactory.TryComplete(_outgoing?.Writer);
			await _loopManager.AwaitWriteLoopAsync().ConfigureAwait(false);
			_loopManager.DisposeCancellationSources();
			_outgoing = null;
		}
		catch (Exception ex)
		{
			ReportError(ex, "Failed to cleanup write loop during cleanup.");
		}

		try
		{
			if (_streams.Stdin != null)
			{
				await StreamInitializer.SafeDisposeAsync(_streams.Stdin).ConfigureAwait(false);
				_streams.Stdin = null;
			}
		}
		catch (Exception ex)
		{
			Error?.Invoke(ex);
		}
	}

	private async Task CreateProcessAndRunPipelinesAsync(
		string                        workingDir,
		TaskCompletionSource<object?> startingSignal,
		CancellationToken             token)
	{
		var startInfo = ProcessHelper.CreateProcessStartInfo(
			_path,
			_args,
			workingDir,
			_options.RedirectStandardError,
			_options.StdoutEncoding,
			_options.StderrEncoding
		);

		var startedProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

		token.ThrowIfCancellationRequested();
		try
		{
			if (!startedProcess.Start()) throw new InvalidOperationException("Failed to start UCI engine process.");
		}
		catch
		{
			startedProcess.Dispose();
			throw;
		}

		_process = startedProcess;
		_stateManager.MarkProcessAlive(true);

		InitializeStreams(startedProcess);
		CreateChannels();
		_loopManager.InitializeCancellationSources();
		StartBackgroundLoops(startedProcess);

		_stateManager.SetStatus(TransportStatus.Started);

		Logger.Log($"UCI engine started. PID={startedProcess.Id}", category: LogCategory.Uci);

		startingSignal.TrySetResult(null);
		await Task.CompletedTask;
	}

	/// <summary>
	///     Orchestrates graceful shutdown: wait for exit, then force kill if necessary.
	/// </summary>
	private async Task HandleGracefulShutdownAsync(Process process, List<Exception> errors)
	{
		if (ProcessHasExitedSafe(process)) return;

		bool exitedGracefully;
		try
		{
			exitedGracefully = await WaitForGracefulExitAsync(process).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			errors.Add(ex);
			exitedGracefully = ProcessHasExitedSafe(process);
		}

		if (!exitedGracefully && !ProcessHasExitedSafe(process))
			await KillProcessAfterGracePeriodAsync(process, errors).ConfigureAwait(false);
	}

	/// <summary>
	///     Force kills the process after the grace period has expired.
	/// </summary>
	private async Task KillProcessAfterGracePeriodAsync(Process process, List<Exception> errors)
	{
		try
		{
			Logger.Log(
				$"Killing UCI engine process after grace period (tree={_options.KillEntireProcessTree}).",
				category: LogCategory.Uci
			);

			KillProcessOrThrow(process);
			await WaitForProcessExitWithTimeoutAsync(
				process,
				_options.TeardownTimeout,
				"forced process termination"
			).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			errors.Add(ex);
		}
	}

	/// <summary>
	///     Immediately kills the process if it hasn't already exited.
	/// </summary>
	private async Task KillProcessImmediatelyAsync(Process process, List<Exception> errors)
	{
		if (ProcessHasExitedSafe(process)) return;

		try
		{
			Logger.Log(
				$"Killing UCI engine process (tree={_options.KillEntireProcessTree}).",
				category: LogCategory.Uci
			);

			KillProcessOrThrow(process);
			await WaitForProcessExitWithTimeoutAsync(
				process,
				_options.TeardownTimeout,
				"failed-start process termination"
			).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			errors.Add(ex);
		}
	}

	/// <summary>
	///     Core teardown logic for stopping or disposing the transport.
	///     Cleans up all resources in the proper order: read loops, write loops, streams, and process.
	/// </summary>
	/// <param name="sendQuit">Whether to send "quit" command before closing stdin.</param>
	/// <param name="finalStatus">The final status to set after teardown completes.</param>
	/// <param name="finalLog">Log message to write after successful teardown.</param>
	private async Task TearDownCoreAsync(
		bool            sendQuit,
		TransportStatus finalStatus,
		string          finalLog)
	{
		// Atomically exchange process reference to avoid race conditions
		var p = Interlocked.Exchange(ref _process, null);
		_stateManager.MarkProcessAlive(false);

		try
		{
			// A child that no longer reads stdin can block a pipe write indefinitely. Begin the
			// bounded process-exit path while joining writes so forced termination releases the pipe.
			// Keep stdout/stderr open until both operations finish, allowing final output to drain.
			await Task.WhenAll(CleanupWriteLoopAndStdinAsync(sendQuit), CleanupProcessAsync(p, false))
				.ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_stateManager.SetStatus(TransportStatus.Failed);
			ReportError(ex, "Failed to teardown UCI transport cleanly");
			throw;
		}
		finally
		{
			// Always close the read side and join the loops after teardown is attempted.
			CancelReadLoopAndDisposeStreams();
			await AwaitReadLoopsCompletionAsync().ConfigureAwait(false);
		}

		_stateManager.SetStatus(finalStatus);
		Logger.Log(finalLog, category: LogCategory.Uci);
	}

	private Task WaitForProcessExitCoreAsync(Process process, CancellationToken ct) =>
		_options.WaitForProcessExitAsyncOverride is { } waitOverride
			? waitOverride(process, ct)
			: ProcessHelper.WaitForProcessExitAsync(process, ct);

	private async Task WaitForProcessExitWithTimeoutAsync(
		Process  process,
		TimeSpan timeout,
		string   operationDescription)
	{
		if (ProcessHasExitedSafe(process)) return;

		using var timeoutCts = timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout);
		var       waitToken  = timeoutCts?.Token ?? CancellationToken.None;

		try
		{
			await WaitForProcessExitCoreAsync(process, waitToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true })
		{
			throw new TimeoutException($"Timed out awaiting {operationDescription}.");
		}

		if (!ProcessHasExitedSafe(process))
			throw new InvalidOperationException($"Process remained alive after {operationDescription}.");
	}

	/// <summary>
	///     Waits for the process to exit gracefully within the grace period.
	/// </summary>
	private async Task<bool> WaitForGracefulExitAsync(Process process)
	{
		if (ProcessHasExitedSafe(process)) return true;

		using var timeoutCts = new CancellationTokenSource(GetQuitGracePeriod());
		try
		{
			await WaitForProcessExitCoreAsync(process, timeoutCts.Token).ConfigureAwait(false);
			return true;
		}
		catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
		{
			return ProcessHasExitedSafe(process);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private TimeSpan GetQuitGracePeriod() =>
		_options.QuitGracePeriod != TimeSpan.Zero
			? _options.QuitGracePeriod
			: TimeSpan.FromMilliseconds(_options.QuitGracePeriodMs);

	/// <summary>
	///     Cancels read loop and disposes stdout/stderr streams (but does not await loop completion).
	/// </summary>
	private void CancelReadLoopAndDisposeStreams()
	{
		try
		{
			_loopManager.CancelReadLoop();
			ChannelFactory.TryComplete(_lines?.Writer);
		}
		catch (Exception ex)
		{
			ReportError(ex, "Failed to cancel read loop during cleanup.");
		}

		try
		{
			StreamInitializer.SafeDispose(_streams.Stdout);
			_streams.Stdout = null;
		}
		catch (Exception ex)
		{
			ReportError(ex, "Failed to dispose stdout during cleanup.");
		}

		try
		{
			StreamInitializer.SafeDispose(_streams.Stderr);
			_streams.Stderr = null;
		}
		catch (Exception ex)
		{
			ReportError(ex, "Failed to dispose stderr during cleanup.");
		}
	}

	private void CleanupExitedProcess()
	{
		if (_process is not { HasExited: true }) return;

		try
		{
			ProcessHelper.SafeDisposeProcess(_process);
		}
		catch (Exception ex)
		{
			// Log but suppress disposal errors for already-exited process
			// This is cleanup of a terminated process, so failures are non-critical
			Logger.Log(ex, category: LogCategory.Uci);
		}
		finally
		{
			_process = null;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void CreateChannels()
	{
		_lines    = ChannelFactory.CreateLinesChannel(_options.ChannelCapacity, _options.SingleReader);
		_outgoing = ChannelFactory.CreateOutgoingChannel(_options.ChannelCapacity, _options.OutgoingSingleWriter);
	}

	/// <summary>
	///     Fires the Exited event exactly once, even if called multiple times.
	///     Handles exceptions from user event handlers to prevent them from propagating.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void FireExitedOnce(int? exitCode, string? error)
	{
		if (!_stateManager.TryMarkExitedRaised()) return;

		var exited = Exited;
		if (exited != null)
			try
			{
				exited(exitCode, error);
			}
			catch (Exception handlerEx)
			{
				// Log and report event handler exceptions, but don't let them propagate
				// to avoid disrupting the transport's internal cleanup logic
				ReportError(handlerEx, $"Exited event handler threw exception: {handlerEx.Message}");
			}
	}

	private void InitializeStreams(Process process)
	{
		var streams = StreamInitializer.InitializeStreams(
			process,
			_options.StdinEncoding,
			_options.StdoutEncoding,
			_options.StderrEncoding,
			_options.RedirectStandardError,
			_options.NewLine,
			false
		);

		_streams.Stdin  = streams.Stdin;
		_streams.Stdout = streams.Stdout;
		_streams.Stderr = streams.Stderr;
	}

	private void KillProcessOrThrow(Process process)
	{
		if (ProcessHasExitedSafe(process)) return;

		try
		{
#if NET5_0_OR_GREATER
			process.Kill(_options.KillEntireProcessTree);
#else
			process.Kill();
#endif
		}
		catch (Exception) when (ProcessHasExitedSafe(process))
		{
			// The process exited between the liveness check and the kill attempt.
		}
	}

	private void PrepareForStart()
	{
		_stateManager.ResetExitedRaised();
		_stateManager.SetStatus(TransportStatus.Starting);
		Logger.Log("Starting UCI engine process.", category: LogCategory.Uci);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ReportError(Exception ex, string message)
	{
		try
		{
			var exception = new Exception($"{message}: {ex.Message}", ex);
			Logger.Log(exception, category: LogCategory.Uci);
		}
		catch
		{
			// Suppress: Logging itself failed - prevent infinite error loop
		}

		try
		{
			Error?.Invoke(ex);
		}
		catch
		{
			// Suppress: User event handler threw - prevent cascading failures
		}
	}

	private void SignalStartFailure(TaskCompletionSource<object?> startingSignal, Exception ex, CancellationToken ct)
	{
		try
		{
			if (ex is OperationCanceledException) startingSignal.TrySetCanceled(ct);
			else startingSignal.TrySetException(ex);
		}
		catch
		{
			// Suppress: TCS operations can throw if already completed (race condition)
			// Failure to signal is non-critical during error handling
		}
	}

	private void StartBackgroundLoops(Process process)
	{
		if (_streams.Stdout != null && _lines != null)
			_loopManager.StartReadLoop(_streams.Stdout, _lines.Writer);

		if (_outgoing != null && _streams.Stdin != null)
			_loopManager.StartWriteLoop(_outgoing.Reader, _streams.Stdin);

		if (_streams.Stderr != null)
			_loopManager.StartStderrLoopIfNeeded(_streams.Stderr);

		_loopManager.StartExitNotification(
			process,
			(exitCode, error) => FireExitedOnce(exitCode, error),
			ex => ReportError(ex, "Error while waiting for process exit."),
			_stateManager
		);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ValidateLineLength(string line)
	{
		if (line.Length > _options.MaxLineLength)
			throw new ArgumentException(
				$"Command line exceeds maximum length of {_options.MaxLineLength} characters. Actual: {line.Length}",
				nameof(line)
			);

		if (line.Length > _options.WarnLineLength)
			Logger.Log(
				$"Warning: Command line length ({line.Length} characters) exceeds warning threshold ({_options.WarnLineLength} characters). This may indicate unusual usage.",
				category: LogCategory.Uci
			);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ValidateTryWritePreconditions(string line, TimeSpan timeout)
	{
		_stateManager.ThrowIfProcessNotStarted();
		_stateManager.ThrowIfDisposed(nameof(ProcessUciTransport));
		_stateManager.ThrowIfProcessNotAlive(_process);
		if (line is null) throw new ArgumentNullException(nameof(line));
		if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
			throw new ArgumentOutOfRangeException(nameof(timeout));

		ValidateLineLength(line);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ValidateWritePreconditions(string line, CancellationToken ct)
	{
		_stateManager.ThrowIfProcessNotStarted();
		_stateManager.ThrowIfDisposed(nameof(ProcessUciTransport));
		_stateManager.ThrowIfProcessNotAlive(_process);
		if (line is null) throw new ArgumentNullException(nameof(line));

		ValidateLineLength(line);

		if (_options.ValidateCommands) ProcessUciTransportValidator.ValidateCommandLine(line);
		ct.ThrowIfCancellationRequested();
	}
}

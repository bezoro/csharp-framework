using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Bezoro.Logging;
using Bezoro.Logging.Types;

namespace Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;

/// <summary>
///     Manages background loops for reading, writing, and monitoring process streams.
/// </summary>
internal sealed class BackgroundLoopManager(
	ProcessUciTransportOptions options,
	Action<Exception, string>  reportError,
	BackgroundLoopMetrics      metrics,
	Action<string>?            stderrReceived = null
)
{
	private const int DEFAULT_FLUSH_BATCH_SIZE = 8;

	private CancellationTokenSource? _readLoopCts;
	private CancellationTokenSource? _writeLoopCts;

	private Task? _exitNotifyTask;
	private Task? _readLoopTask;
	private Task? _stderrLoopTask;
	private Task? _writeLoopTask;

	public long BackpressureEvents => metrics.BackpressureEvents;
	public long LinesRead          => metrics.LinesRead;
	public long LinesWritten       => metrics.LinesWritten;

	/// <summary>
	///     Checks if loops are healthy.
	/// </summary>
	public bool AreLoopsHealthy() =>
		(_readLoopTask is null ||
		 !(_readLoopTask.IsCompleted || _readLoopTask.IsFaulted || _readLoopTask.IsCanceled)) &&
		(_writeLoopTask is null ||
		 !(_writeLoopTask.IsCompleted || _writeLoopTask.IsFaulted || _writeLoopTask.IsCanceled)) &&
		(_stderrLoopTask is null ||
		 !(_stderrLoopTask.IsCompleted || _stderrLoopTask.IsFaulted || _stderrLoopTask.IsCanceled)) &&
		(_exitNotifyTask is null ||
		 !(_exitNotifyTask.IsCompleted || _exitNotifyTask.IsFaulted || _exitNotifyTask.IsCanceled));

	/// <summary>
	///     Awaits exit notification task.
	/// </summary>
	public async Task AwaitExitNotificationAsync(TimeSpan timeout)
	{
		var task = _exitNotifyTask;
		_exitNotifyTask = null;
		if (task is null) return;

		if (timeout <= TimeSpan.Zero)
		{
			await task.ConfigureAwait(false);
			return;
		}

		var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
		if (completed != task)
			throw new TimeoutException("Timed out awaiting process exit notification.");

		await task.ConfigureAwait(false);
	}

	/// <summary>
	///     Awaits read loop task.
	/// </summary>
	public async Task AwaitReadLoopAsync()
	{
		var task = _readLoopTask;
		_readLoopTask = null;
		await AwaitLoopTaskAsync(task, "Failed to join read loop.").ConfigureAwait(false);
	}

	/// <summary>
	///     Awaits stderr loop task.
	/// </summary>
	public async Task AwaitStderrLoopAsync()
	{
		var task = _stderrLoopTask;
		_stderrLoopTask = null;
		await AwaitLoopTaskAsync(task, "Failed to join stderr loop.").ConfigureAwait(false);
	}

	/// <summary>
	///     Awaits write loop task.
	/// </summary>
	public async Task AwaitWriteLoopAsync()
	{
		var task = _writeLoopTask;
		_writeLoopTask = null;
		await AwaitLoopTaskAsync(task, "Failed to join write loop.").ConfigureAwait(false);
	}

	/// <summary>
	///     Cancels read loop cancellation token.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void CancelReadLoop()
	{
		try
		{
			_readLoopCts?.Cancel();
		}
		catch
		{
			// CancellationTokenSource.Cancel() can throw ObjectDisposedException if already disposed
		}
	}

	/// <summary>
	///     Cancels write loop cancellation token.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void CancelWriteLoop()
	{
		try
		{
			_writeLoopCts?.Cancel();
		}
		catch
		{
			// CancellationTokenSource.Cancel() can throw ObjectDisposedException if already disposed
		}
	}

	/// <summary>
	///     Disposes cancellation token sources.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void DisposeCancellationSources()
	{
		try
		{
			_readLoopCts?.Dispose();
			_writeLoopCts?.Dispose();
		}
		catch
		{
			// Dispose can throw in rare race conditions; ensure fields are nulled in finally
		}
		finally
		{
			_readLoopCts  = null;
			_writeLoopCts = null;
		}
	}

	/// <summary>
	///     Initializes cancellation token sources for the loops.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void InitializeCancellationSources()
	{
		_readLoopCts  = new();
		_writeLoopCts = new();
	}

	/// <summary>
	///     Starts exit notification monitoring.
	/// </summary>
	public void StartExitNotification(
		Process               process,
		Action<int?, string?> onExited,
		Action<Exception>     reportExitError,
		TransportStateManager stateManager)
	{
		_exitNotifyTask = Task.Run(
			async () =>
			{
				try
				{
					if (options.WaitForProcessExitAsyncOverride is { } waitOverride)
						await waitOverride(process, CancellationToken.None).ConfigureAwait(false);
					else
						await ProcessHelper.WaitForProcessExitAsync(process, CancellationToken.None)
										   .ConfigureAwait(false);

					stateManager.MarkProcessAlive(false);
					int exitCode = process.ExitCode;
					Logger.Log($"UCI engine process exited with code {exitCode}.", category: LogCategory.Uci);
					onExited(exitCode, null);
				}
				catch (Exception ex)
				{
					reportExitError(ex);
					onExited(null, ex.Message);
				}
			},
			CancellationToken.None
		);

		Observe(_exitNotifyTask);
	}

	/// <summary>
	///     Starts the read loop.
	/// </summary>
	public void StartReadLoop(StreamReader stdout, ChannelWriter<string> writer)
	{
		var readToken = _readLoopCts!.Token;

		_readLoopTask = RunAsync();
		Observe(_readLoopTask);

		async Task RunAsync()
		{
			try
			{
				await StartReader(stdout,
					() => RunSynchronousReadLoop(stdout, writer, readToken),
					() => RunReadLoopAsync(stdout, writer, readToken)).ConfigureAwait(false);
				ChannelFactory.TryComplete(writer);
			}
			catch (OperationCanceledException)
			{
				// Expected when cancellation is requested; complete the channel gracefully
				ChannelFactory.TryComplete(writer);
			}
			catch (Exception ex)
			{
				await Task.Run(() => reportError(ex, "Read loop faulted.")).ConfigureAwait(false);
				ChannelFactory.TryComplete(writer, ex);
			}
		}
	}

	/// <summary>
	///     Starts the stderr loop if needed.
	/// </summary>
	public void StartStderrLoopIfNeeded(StreamReader? stderr)
	{
		if (stderr == null) return;

		_stderrLoopTask = RunAsync();
		Observe(_stderrLoopTask);

		async Task RunAsync()
		{
			try
			{
				await StartReader(stderr,
					() => RunSynchronousStderrLoop(stderr),
					() => RunStderrLoopAsync(stderr)).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				await Task.Run(() => reportError(ex, "Stderr loop faulted.")).ConfigureAwait(false);
			}
		}
	}

	private static Task StartReader(StreamReader reader, Action synchronousRead, Func<Task> asynchronousRead)
	{
		// Mono and other synchronous pipe backends emulate ReadLineAsync with blocking
		// pool work. Keep those reads on owned threads so idle engines cannot starve
		// command writers. Genuine async/custom streams retain their async I/O path.
		return reader.BaseStream is FileStream { IsAsync: false } or PipeStream { IsAsync: false }
			? Task.Factory.StartNew(synchronousRead, CancellationToken.None,
				TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default)
			: Task.Run(asynchronousRead);
	}

	/// <summary>
	///     Starts the write loop.
	/// </summary>
	public void StartWriteLoop(ChannelReader<string> outgoingReader, StreamWriter stdin)
	{
		if (options.DisableWriteLoop) return;

		var writeToken = _writeLoopCts!.Token;

		_writeLoopTask = Task.Run(
			async () =>
			{
				try
				{
					await RunWriteLoopAsync(outgoingReader, stdin, writeToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					// Expected when cancellation is requested
				}
				catch (Exception ex)
				{
					reportError(ex, "Write loop faulted.");
				}
			},
			CancellationToken.None
		);

		Observe(_writeLoopTask);
	}

	/// <summary>
	///     Attempts to write a line to the stream, returning false if the stream is disposed.
	/// </summary>
	private static bool TryWriteLine(StreamWriter writer, string line)
	{
		try
		{
			writer.WriteLine(line);
			return true;
		}
		catch (ObjectDisposedException)
		{
			return false;
		}
	}

	/// <summary>
	///     Attempts to read a line from the stream, returning null if the stream is closed or disposed.
	/// </summary>
	private static async ValueTask<string?> TryReadLineAsync(StreamReader reader)
	{
		try
		{
			return await reader.ReadLineAsync().ConfigureAwait(false);
		}
		catch (ObjectDisposedException)
		{
			return null;
		}
	}

	private static string? TryReadLine(StreamReader reader)
	{
		try { return reader.ReadLine(); }
		catch (ObjectDisposedException) { return null; }
	}

	/// <summary>
	///     Attempts to flush the stream, swallowing any exceptions.
	/// </summary>
	private static void TryFlush(StreamWriter writer)
	{
		try
		{
			writer.Flush();
		}
		catch
		{
			// Flush can throw if stream is closed/disposed; safe to ignore
		}
	}

	/// <summary>
	///     Processes all available commands from the channel, writing them to stdin with batched flushing.
	///     Returns the number of writes since the last flush.
	/// </summary>
	private int ProcessAvailableCommands(
		ChannelReader<string> outgoingReader,
		StreamWriter          stdin,
		int                   flushBatchSize,
		CancellationToken     writeToken)
	{
		var writesSinceFlush = 0;

		while (outgoingReader.TryRead(out string? cmd))
		{
			if (writeToken.IsCancellationRequested || !TryWriteLine(stdin, cmd))
				return writesSinceFlush;

			metrics.IncrementLinesWritten();

			if (++writesSinceFlush < flushBatchSize) continue;

			TryFlush(stdin);
			writesSinceFlush = 0;
		}

		return writesSinceFlush;
	}

	/// <summary>
	///     Awaits a loop task and reports any errors.
	/// </summary>
	private async Task AwaitLoopTaskAsync(Task? task, string errorDescription)
	{
		if (task != null)
			try
			{
				await task.ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				reportError(ex, errorDescription);
			}
	}

	/// <summary>
	///     Handles backpressure when the channel is full by waiting for space to become available.
	/// </summary>
	private async Task HandleBackpressureAsync(ChannelWriter<string> writer, string line, CancellationToken token)
	{
		metrics.IncrementBackpressureEvents();

		while (true)
		{
			if (token.IsCancellationRequested)
				throw new OperationCanceledException(token);

			bool canWrite = await writer.WaitToWriteAsync(token).ConfigureAwait(false);

			if (!canWrite) break;
			if (writer.TryWrite(line)) break;
		}
	}

	/// <summary>
	///     Runs the read loop, reading lines from stdout and writing to the channel.
	/// </summary>
	private async Task RunReadLoopAsync(StreamReader stdout, ChannelWriter<string> writer, CancellationToken readToken)
	{
		while (true)
		{
			if (readToken.IsCancellationRequested) break;

			string? line = await TryReadLineAsync(stdout).ConfigureAwait(false);
			if (line is null) break;

			if (line.Length == 0) continue;

			metrics.IncrementLinesRead();

			if (writer.TryWrite(line)) continue;

			await HandleBackpressureAsync(writer, line, readToken).ConfigureAwait(false);
		}
	}

	private void RunSynchronousReadLoop(StreamReader stdout, ChannelWriter<string> writer, CancellationToken readToken)
	{
		while (!readToken.IsCancellationRequested)
		{
			string? line = TryReadLine(stdout);
			if (line is null) break;
			if (line.Length == 0) continue;
			metrics.IncrementLinesRead();
			if (writer.TryWrite(line)) continue;

#pragma warning disable VSTHRD002 // Only the owned blocking pipe reader waits; pool/UI threads remain free.
			HandleBackpressureAsync(writer, line, readToken).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
		}
	}

	/// <summary>
	///     Runs the stderr loop, reading lines and invoking the handler.
	/// </summary>
	private async Task RunStderrLoopAsync(StreamReader stderr)
	{
		while (true)
		{
			string? line = await TryReadLineAsync(stderr).ConfigureAwait(false);
			if (line is null) break;

			if (line.Length == 0) continue;

			InvokeStderrHandler(line);
		}
	}

	private void RunSynchronousStderrLoop(StreamReader stderr)
	{
		while (true)
		{
			string? line = TryReadLine(stderr);
			if (line is null) break;
			if (line.Length == 0 || stderrReceived is null) continue;

			// Join each pool callback before reading its successor, preserving order and
			// the callback thread contract without an unbounded stderr queue.
#pragma warning disable VSTHRD002 // This wait belongs to the owned stderr reader, never a pool/UI thread.
			Task.Run(() => InvokeStderrHandler(line)).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
		}
	}

	private void InvokeStderrHandler(string line)
	{
		try { stderrReceived?.Invoke(line); }
		catch { /* User-provided handler exceptions must not crash the loop. */ }
	}

	/// <summary>
	///     Runs the write loop, reading commands from the channel and writing to stdin.
	/// </summary>
	private async Task RunWriteLoopAsync(
		ChannelReader<string> outgoingReader,
		StreamWriter          stdin,
		CancellationToken     writeToken)
	{
		int flushBatchSize = options.FlushBatchSize > 0 ? options.FlushBatchSize : DEFAULT_FLUSH_BATCH_SIZE;

		while (true)
		{
			int writesSinceFlush = ProcessAvailableCommands(outgoingReader, stdin, flushBatchSize, writeToken);

			if (writesSinceFlush > 0)
				TryFlush(stdin);

			if (writeToken.IsCancellationRequested) break;

			bool hasMore = await outgoingReader.WaitToReadAsync(writeToken).ConfigureAwait(false);
			if (!hasMore) break;
		}
	}

	/// <summary>
	///     Observes a task for faults.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void Observe(Task task)
	{
		task.ContinueWith(
			t =>
			{
				try
				{
					var agg = t.Exception!.Flatten();
					var ex  = agg.InnerExceptions.Count == 1 ? agg.InnerExceptions[0] : agg;
					reportError(ex, "Background task faulted.");
				}
				catch
				{
					// reportError itself may throw; prevent crashes during error reporting
				}
			},
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
			TaskScheduler.Default
		);
	}
}

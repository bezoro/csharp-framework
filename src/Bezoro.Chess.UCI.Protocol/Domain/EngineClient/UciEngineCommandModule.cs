using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.Chess.UCI.Protocol.Domain.Common.Constants;

namespace Bezoro.Chess.UCI.Protocol.Domain.EngineClient;

/// <summary>
///     Provides reusable command helpers for interacting with a UCI engine transport.
/// </summary>
internal sealed class UciEngineCommandModule(
	IUciTransport          transport,
	UciLineWaiterRegistry  lineWaiters,
	Action<EngineActivity> setActivity,
	UciClientOptions       options,
    Action? ensureUsable = null
)
{
	private readonly Action<EngineActivity> _setActivity =
		setActivity ?? throw new ArgumentNullException(nameof(setActivity));
	// Protected by _protocolGate. Standard UCI acknowledgments carry no request identity.
	private bool _requiresRestart;
	private readonly IUciTransport  _transport    = transport ?? throw new ArgumentNullException(nameof(transport));
	private readonly SemaphoreSlim  _protocolGate = new(1, 1);
	private readonly UciClientOptions _options = options;
	private readonly UciLineWaiterRegistry _lineWaiters =
		lineWaiters ?? throw new ArgumentNullException(nameof(lineWaiters));


	public async Task IsReadyAsync(CancellationToken ct)
	{
		await ExecuteSerializedCommandAsync(IsReadyCoreAsync, ct).ConfigureAwait(false);
	}

	public async Task PonderHitAsync(CancellationToken ct)
	{
        ensureUsable?.Invoke();
		await _transport.WriteLineAsync(UciConstants.Commands.PONDER_HIT, ct).ConfigureAwait(false);
		_setActivity(EngineActivity.Searching);
	}

	public async Task RegisterAsync(UciRegistration registration, CancellationToken ct)
	{
		await ExecuteSerializedCommandAsync(
			token => _transport.WriteLineAsync(UciCommandBuilder.BuildRegisterCommand(registration), token),
			ct
		).ConfigureAwait(false);
	}

	public async Task SetDebugAsync(bool enabled, CancellationToken ct)
	{
		await ExecuteSerializedCommandAsync(
			token => _transport.WriteLineAsync(UciCommandBuilder.BuildDebugCommand(enabled), token),
			ct
		).ConfigureAwait(false);
	}

	public async Task SetOptionAsync(string name, string? value, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(name))
			throw new ArgumentException("Option name must be provided.", nameof(name));

		await ExecuteSerializedCommandAsync(
			async token =>
			{
				await _transport.WriteLineAsync(UciCommandBuilder.BuildSetOptionCommand(name, value), token)
								.ConfigureAwait(false);

				await IsReadyCoreAsync(token).ConfigureAwait(false);
			},
			ct
		).ConfigureAwait(false);
	}

	public async Task SetPositionAsync(Fen fen, IEnumerable<string>? moves, CancellationToken ct)
    {
        string command = PreparePositionCommand(fen, moves, ct);
        await SetPreparedPositionAsync(command, ct).ConfigureAwait(false);
    }

    internal string PreparePositionCommand(Fen fen, IEnumerable<string>? moves, CancellationToken ct)
    {
        ensureUsable?.Invoke();
        ct.ThrowIfCancellationRequested();
        if (!Fen.Validate(fen.Raw)) throw new ArgumentException("Invalid FEN provided.", nameof(fen));
        return UciCommandBuilder.BuildPositionCommand(fen, moves);
    }

    internal Task SetPreparedPositionAsync(string command, CancellationToken ct)
    {
        ensureUsable?.Invoke();
        ct.ThrowIfCancellationRequested();
        return _transport.WriteLineAsync(command, ct);
    }


	public async Task UciInitAsync(CancellationToken ct, bool newTransportSession = false)
	{
		await ExecuteSerializedCommandAsync(UciInitCoreAsync, ct, newTransportSession).ConfigureAwait(false);
	}

	public async Task UciNewGameAsync(CancellationToken ct)
	{
		await ExecuteSerializedCommandAsync(UciNewGameCoreAsync, ct).ConfigureAwait(false);
	}

	public async Task<Fen?> TryGetFenViaDisplayBoardAsync(CancellationToken ct) =>
		await ExecuteSerializedCommandAsync(GetFenViaDisplayBoardCoreAsync, ct).ConfigureAwait(false);

	public async Task<ImmutableArray<string>> GetLegalMovesViaPerftAsync(CancellationToken ct) =>
		await ExecuteSerializedCommandAsync(GetLegalMovesViaPerftCoreAsync, ct).ConfigureAwait(false);

	private static bool IsCheckersLine(string line) =>
		line.AsSpan().TrimStart().StartsWith(UciConstants.Prefixes.CHECKERS, StringComparison.OrdinalIgnoreCase);

	private static bool IsFenLine(string line) =>
		line.AsSpan().TrimStart().StartsWith(UciConstants.Prefixes.FEN, StringComparison.OrdinalIgnoreCase);

	private async Task ExecuteSerializedCommandAsync(Func<CancellationToken, Task> action, CancellationToken ct, bool newTransportSession = false)
	{
        ensureUsable?.Invoke();
		await _protocolGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
            ensureUsable?.Invoke();
			ct.ThrowIfCancellationRequested();
			if (newTransportSession) _requiresRestart = false;
			EnsureAcknowledgmentsUsable();
			await action(ct).ConfigureAwait(false);
		}
		finally
		{
			_protocolGate.Release();
		}
	}

	private async Task IsReadyCoreAsync(CancellationToken ct)
	{
		await WriteAndWaitAsync(
			UciConstants.Commands.IS_READY,
			IsReadyResponse,
			_options.ReadyTimeout,
			ct
		).ConfigureAwait(false);
	}

	private async Task UciInitCoreAsync(CancellationToken ct)
	{
		await WriteAndWaitAsync(
			UciConstants.Commands.UCI,
			static l => l.Trim().Equals(UciConstants.Responses.UCI_OK, StringComparison.OrdinalIgnoreCase),
			_options.UciHandshakeTimeout,
			ct
		).ConfigureAwait(false);

		await IsReadyCoreAsync(ct).ConfigureAwait(false);
	}

	private void EnsureAcknowledgmentsUsable()
	{
		if (_requiresRestart)
			throw new InvalidOperationException("A protocol acknowledgment is unaccounted for. Stop and restart the engine before issuing another serialized command.");
	}

	private static bool IsReadyResponse(string line) =>
		line.AsSpan().Trim().Equals(UciConstants.Responses.READY_OK.AsSpan(), StringComparison.OrdinalIgnoreCase);

	private async Task WriteAndWaitAsync(string command, Func<string, bool> predicate, TimeSpan timeout, CancellationToken ct,
		string? precedingCommand = null)
	{
		using var waiterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		waiterCts.CancelAfter(timeout);
		var token = waiterCts.Token;
		token.ThrowIfCancellationRequested();
		var response = _lineWaiters.WaitForAsync(predicate, Timeout.InfiniteTimeSpan, token);
		try
		{
			if (precedingCommand is not null)
				await _transport.WriteLineAsync(precedingCommand, token).ConfigureAwait(false);
			await _transport.WriteLineAsync(command, token).ConfigureAwait(false);
			await response.ConfigureAwait(false);
		}
		catch
		{
			_requiresRestart = true;
			throw;
		}
		finally
		{
			// Join waiter removal before releasing the serialized command gate.
			waiterCts.Cancel();
			try { await response.ConfigureAwait(false); }
			catch { /* The original write/wait error remains authoritative. */ }
		}
	}

	private async Task UciNewGameCoreAsync(CancellationToken ct)
	{
		await _transport.WriteLineAsync(UciConstants.Commands.UCI_NEW_GAME, ct).ConfigureAwait(false);
		await IsReadyCoreAsync(ct).ConfigureAwait(false);
	}

	private async Task<Fen?> GetFenViaDisplayBoardCoreAsync(CancellationToken ct)
	{
		using var waiterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		using var checkersCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var fenTask = _lineWaiters.WaitForAsync(static line => IsFenLine(line), _options.DisplayBoardFenTimeout, waiterCts.Token);
		var checkersTask = _lineWaiters.WaitForAsync(static line => IsCheckersLine(line), Timeout.InfiniteTimeSpan, checkersCts.Token);
		try
		{
			await _transport.WriteLineAsync(UciConstants.Commands.DISPLAY_BOARD, ct).ConfigureAwait(false);

			string fenLine;
			try { fenLine = await fenTask.ConfigureAwait(false); }
			catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }

			if (!checkersTask.IsCompleted)
				checkersCts.CancelAfter(_options.DisplayBoardCheckersGracePeriod);

			string? checkersLine;
			try { checkersLine = await checkersTask.ConfigureAwait(false); }
			catch (OperationCanceledException) when (!ct.IsCancellationRequested) { checkersLine = null; }

			string? rawFenCache = null;
			_ = Fen.TryParseUciOutputLine(fenLine, ref rawFenCache, out var fenFromFen);
			if (checkersLine is null) return fenFromFen;
			_ = Fen.TryParseUciOutputLine(checkersLine, ref rawFenCache, out var fenWithCheckers);
			return fenWithCheckers;
		}
		finally
		{
			waiterCts.Cancel();
			checkersCts.Cancel();
			try { await fenTask.ConfigureAwait(false); } catch { /* Observe and remove the waiter. */ }
			try { await checkersTask.ConfigureAwait(false); } catch { /* Observe and remove the waiter. */ }
		}
	}

	private async Task<ImmutableArray<string>> GetLegalMovesViaPerftCoreAsync(CancellationToken ct)
	{
		var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		bool CaptureUntilReady(string line)
		{
			if (IsReadyResponse(line)) return true;
			UciCommandBuilder.CollectMovesFromLine(line, results);
			return false;
		}

		// The read loop owns the collection until it removes this waiter on readyok.
		await WriteAndWaitAsync(UciConstants.Commands.IS_READY, CaptureUntilReady, _options.ReadyTimeout, ct,
			precedingCommand: UciConstants.Commands.GO_PERFT + " 1").ConfigureAwait(false);

		return [.. results];
	}

	private async Task<T> ExecuteSerializedCommandAsync<T>(
		Func<CancellationToken, Task<T>> action,
		CancellationToken                ct)
	{
        ensureUsable?.Invoke();
		await _protocolGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
            ensureUsable?.Invoke();
			ct.ThrowIfCancellationRequested();
			EnsureAcknowledgmentsUsable();
			return await action(ct).ConfigureAwait(false);
		}
		finally
		{
			_protocolGate.Release();
		}
	}
}

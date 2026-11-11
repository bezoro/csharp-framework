using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.Chess.UCI.Protocol.Domain.Common.Constants;
using Bezoro.Chess.UCI.Protocol.Domain.EngineClient;

namespace Bezoro.Chess.UCI.Protocol.API;

/// <summary>
///     Provides high-level async orchestration and lifecycle management for a UCI engine process/transport.
///     Maintains a single search session at a time, parses UCI output, tracks engine activity state, and
///     exposes events and helpers for UCI clients.
/// </summary>
public sealed class UciEngineClient : IAsyncDisposable, IUciLineSource
{
	private readonly EngineActivityTracker _activityTracker = new();
	/// <summary>
	///     Underlying transport abstraction for communicating with the engine.
	/// </summary>
	private readonly IUciTransport _transport;
	private readonly object                 _lifecycleLock = new();
	private readonly SemaphoreSlim          _lifecycleGate = new(1, 1);
	private bool _disposed;
	private readonly object                 _metadataLock  = new();
	private readonly UciEngineCommandModule _commandModule;

	/// <summary>
	///     Registry for awaiting specific engine output lines.
	/// </summary>
	private readonly UciLineWaiterRegistry _lineWaiters = new();
	private readonly UciOutputDispatcher _outputDispatcher;

	private readonly UciSearchCoordinator _searchCoordinator;
	private readonly UciClientOptions     _options;

	/// <summary>
	///     Cancellation source for the read loop and engine lifetime.
	/// </summary>
	private CancellationTokenSource? _cts;
	private ImmutableArray<UciEngineOption> _availableOptions = ImmutableArray<UciEngineOption>.Empty;

	private Task?                 _readerTask;
	private UciEngineCapabilities _capabilities = UciEngineCapabilities.Unknown;
	private UciEngineInfo         _engineInfo   = UciEngineInfo.Empty;

	/// <summary>
	///     Occurs when the engine transitions between <see cref="EngineActivity" /> states.
	/// </summary>
	public event Action<EngineActivity, EngineActivity>? ActivityChanged
	{
		add => _activityTracker.ActivityChanged += value;
		remove => _activityTracker.ActivityChanged -= value;
	}

	/// <summary>
	///     Notifies when the engine emits a parsed <c>bestmove</c> line.
	/// </summary>
	public event Action<UciBestMoveMessage>? BestMoveMessageReceived;

	/// <summary>
	///     Compatibility event for callers that only need the best move and ponder move strings.
	/// </summary>
	/// <remarks>Use <see cref="BestMoveMessageReceived" /> instead.</remarks>
	[Obsolete("Use BestMoveMessageReceived instead.", false)]
	public event Action<string, string>? BestMoveReceived;

	/// <summary>
	///     Raised when the transport or client internals observe an unexpected protocol-processing failure.
	/// </summary>
	public event Action<Exception>? Error;

	/// <summary>
	///     Raised for parsed UCI <c>info</c> lines.
	/// </summary>
	public event Action<UciInfoMessage>? InfoReceived;

	/// <summary>
	///     Compatibility event raised for principal-variation-bearing <c>info ... pv ...</c> lines.
	/// </summary>
	public event Action<PrincipalVariation>? InfoPvReceived;

	/// <summary>
	///     Raised for every raw output line received from the engine.
	/// </summary>
	public event Action<string>? RawLineReceived;

	/// <summary>
	///     Compatibility event alias for <see cref="RawLineReceived" />.
	/// </summary>
	/// <remarks>Use <see cref="RawLineReceived" /> instead.</remarks>
	[Obsolete("Use RawLineReceived instead.", false)]
	public event Action<string>? LineReceived;

	/// <summary>
	///     Raised for every parsed protocol message received from the engine.
	/// </summary>
	public event Action<UciProtocolMessage>? ProtocolMessageReceived;

	/// <summary>
	///     Raised for lines received on redirected stderr.
	/// </summary>
	public event Action<string>? StderrReceived;

	/// <summary>
	///     Initializes a new client backed by a process transport for the supplied engine executable.
	/// </summary>
	/// <param name="enginePath">Path to the engine executable.</param>
	/// <param name="args">Optional process arguments passed to the engine.</param>
	/// <param name="workingDirectory">Optional working directory used when starting the engine process.</param>
	/// <param name="options">Optional client-level timeout and parsing overrides.</param>
	public UciEngineClient(
		string               enginePath,
		IEnumerable<string>? args = null,
		string?              workingDirectory = null,
		UciClientOptions?    options = null)
		: this(new ProcessUciTransport(enginePath, args, workingDirectory), options) { }

	/// <summary>Creates a client over an application-provided transport, such as a native bridge or remote engine.</summary>
	/// <param name="transport">Transport whose lifecycle is owned and disposed by this client.</param>
	/// <param name="options">Optional handshake, readiness, and search policy.</param>
	public UciEngineClient(IUciTransport transport, UciClientOptions? options = null)
	{
		_transport                = transport ?? throw new ArgumentNullException(nameof(transport));
		_options                  = options ?? new UciClientOptions();
		if (_options.MaxCapturedPrincipalVariations <= 0)
			throw new ArgumentOutOfRangeException(nameof(options), "MaxCapturedPrincipalVariations must be positive.");
		_transport.Error         += OnTransportError;
		_transport.StderrReceived += OnTransportStderr;

		_searchCoordinator = new(
			_transport,
			SetActivity,
			_options
		);

		_outputDispatcher = new(_lineWaiters, _searchCoordinator, ObserveProtocolMessage);
		_commandModule    = new(_transport, _lineWaiters, this, SetActivity, _options, ThrowIfDisposed);
	}

	/// <summary>
	///     Indicates whether the underlying transport is healthy.
	/// </summary>
	public bool IsHealthy => _transport.IsHealthy;

	/// <summary>
	///     Returns true after <see cref="StartAsync" /> is called and until <see cref="StopAsync" /> completes.
	/// </summary>
	public bool IsStarted => _transport.IsStarted;

	/// <summary>
	///     Gets the current engine activity state.
	/// </summary>
	public EngineActivity Activity => _activityTracker.Current;

	/// <summary>
	///     Gets the client-level timeout and parsing configuration.
	/// </summary>
	public UciClientOptions Options => _options;

	/// <summary>
	///     Gets the options advertised by the engine during handshake.
	/// </summary>
	public ImmutableArray<UciEngineOption> AvailableOptions
	{
		get
		{
			lock (_metadataLock)
			{
				return _availableOptions;
			}
		}
	}

	/// <summary>
	///     Engine process/transport status.
	/// </summary>
	public TransportStatus Status => _transport.Status;

	/// <summary>
	///     Gets the standard and extension capabilities discovered for the current engine.
	/// </summary>
	public UciEngineCapabilities Capabilities
	{
		get
		{
			lock (_metadataLock)
			{
				return _capabilities;
			}
		}
	}

	/// <summary>
	///     Gets the engine metadata captured from <c>id name</c> and <c>id author</c> output.
	/// </summary>
	public UciEngineInfo EngineInfo
	{
		get
		{
			lock (_metadataLock)
			{
				return _engineInfo;
			}
		}
	}

	/// <summary>
	///     Determines if a string is a valid UCI move (e.g. "e2e4", "a7a8q").
	/// </summary>
	public static bool IsUciMoveString(string s) => UciCommandBuilder.IsUciMoveString(s);

	/// <summary>
	///     Builds a UCI-compliant "go ..." command from <paramref name="parameters" />.
	/// </summary>
	/// <param name="parameters">Search configuration</param>
	/// <returns>Full "go ..." line to send to engine</returns>
	public static string BuildGoCommand(SearchParameters parameters) =>
		UciCommandBuilder.BuildGoCommand(parameters);

	/// <summary>
	///     Attempts to retrieve an advertised engine option by name.
	/// </summary>
	public bool TryGetOption(string name, out UciEngineOption option)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			option = default;
			return false;
		}

		lock (_metadataLock)
		{
			foreach (var availableOption in _availableOptions)
			{
				if (!string.Equals(availableOption.Name, name, StringComparison.OrdinalIgnoreCase)) continue;

				option = availableOption;
				return true;
			}
		}

		option = default;
		return false;
	}

	/// <summary>
	///     Attempts to read the advertised Elo range for engines that support UCI strength limiting.
	/// </summary>
	/// <param name="minElo">Minimum advertised Elo when available.</param>
	/// <param name="maxElo">Maximum advertised Elo when available.</param>
	/// <returns>
	///     <see langword="true" /> when the engine advertises both <c>UCI_LimitStrength</c> and a bounded
	///     <c>UCI_Elo</c> option; otherwise <see langword="false" />.
	/// </returns>
	public bool TryGetStrengthLimitRange(out int minElo, out int maxElo)
	{
		minElo = default;
		maxElo = default;

		if (!TryGetStrengthOptions(out _, out var eloOption))
			return false;

		if (eloOption.Min is not int min || eloOption.Max is not int max)
			return false;

		minElo = min;
		maxElo = max;
		return true;
	}

	/// <summary>
	///     Enables Elo-limited play for engines that advertise the standard <c>UCI_LimitStrength</c> and
	///     <c>UCI_Elo</c> options.
	/// </summary>
	/// <param name="elo">Desired engine Elo.</param>
	/// <param name="ct">Cancellation token for the composed option updates.</param>
	/// <exception cref="ArgumentOutOfRangeException">
	///     Thrown when <paramref name="elo" /> falls outside the engine's advertised range.
	/// </exception>
	/// <exception cref="NotSupportedException">
	///     Thrown when the engine does not advertise the standard strength-limit options.
	/// </exception>
	public async Task SetStrengthLimitAsync(int elo, CancellationToken ct = default)
	{
		if (!TryGetStrengthOptions(out var limitStrengthOption, out var eloOption))
		{
			throw new NotSupportedException(
				"Engine does not advertise the standard UCI_LimitStrength and UCI_Elo options required for Elo-limited play."
			);
		}

		if (eloOption.Min is int minElo && elo < minElo)
		{
			throw new ArgumentOutOfRangeException(
				nameof(elo),
				elo,
				$"Engine strength Elo must be greater than or equal to {minElo.ToString(CultureInfo.InvariantCulture)}."
			);
		}

		if (eloOption.Max is int maxElo && elo > maxElo)
		{
			throw new ArgumentOutOfRangeException(
				nameof(elo),
				elo,
				$"Engine strength Elo must be less than or equal to {maxElo.ToString(CultureInfo.InvariantCulture)}."
			);
		}

		await SetOptionAsync(limitStrengthOption.Name, "true", ct).ConfigureAwait(false);
		await SetOptionAsync(eloOption.Name, elo.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
	}

	/// <summary>Disables the standard Elo strength limit when the engine advertises that option.</summary>
	/// <param name="ct">Cancellation token for the option update.</param>
	/// <remarks>Engines without the standard check option require no update.</remarks>
	public Task ClearStrengthLimitAsync(CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		return TryGetOption(UciConstants.Options.LIMIT_STRENGTH, out var option) && option.Type == UciOptionType.Check
			? SetOptionAsync(option.Name, "false", ct) : Task.CompletedTask;
	}

	/// <summary>
	///     Starts a search with the given parameters and does not await engine termination nor the bestmove response.
	///     Use for fire-and-forget searches (e.g., GUI spinning).
	/// </summary>
	public Task GoFireAndForgetAsync(SearchParameters parameters, CancellationToken ct) =>
		Searches.ExecuteFireAndForgetAsync(parameters, ct);

	/// <summary>
	///     Sends "isready" to the engine and waits up to 10 seconds for "readyok".
	/// </summary>
	public Task IsReadyAsync(CancellationToken ct) => _commandModule.IsReadyAsync(ct);

	/// <summary>
	///     Sends the standard UCI <c>ponderhit</c> command.
	/// </summary>
	public Task PonderHitAsync(CancellationToken ct) => _commandModule.PonderHitAsync(ct);

	/// <summary>
	///     Sends the standard UCI <c>register</c> command.
	/// </summary>
	public Task RegisterAsync(UciRegistration registration, CancellationToken ct) =>
		_commandModule.RegisterAsync(registration, ct);

	/// <summary>
	///     Sends the standard UCI <c>debug on/off</c> command.
	/// </summary>
	public Task SetDebugAsync(bool enabled, CancellationToken ct) => _commandModule.SetDebugAsync(enabled, ct);

	/// <summary>
	///     Sends a "setoption" command to the engine.
	/// </summary>
	public Task SetOptionAsync(string name, string? value, CancellationToken ct) =>
		_commandModule.SetOptionAsync(name, value, ct);

	/// <summary>
	///     Sets the board position using a FEN and (optionally) a move list.
	/// </summary>
	public Task SetPositionAsync(Fen fen, IEnumerable<string>? moves, CancellationToken ct) =>
		_commandModule.SetPositionAsync(fen, moves, ct);

    internal string PreparePositionCommand(Fen fen, IEnumerable<string>? moves, CancellationToken ct) =>
        _commandModule.PreparePositionCommand(fen, moves, ct);

    internal Task SetPreparedPositionAsync(string command, CancellationToken ct) =>
        _commandModule.SetPreparedPositionAsync(command, ct);


	/// <summary>
	///     Starts the engine process/connection, background read loop, and completes UCI handshake.
	/// </summary>
	public async Task StartAsync(CancellationToken ct = default)
	{
		await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			if (_disposed) throw new ObjectDisposedException(nameof(UciEngineClient));
			await StartCoreAsync(ct).ConfigureAwait(false);
		}
		finally { _lifecycleGate.Release(); }
	}

	private async Task StartCoreAsync(CancellationToken ct)
	{
		lock (_lifecycleLock)
		{
			if (_cts is { })
				throw new InvalidOperationException("Engine client is already started.");

			ct.ThrowIfCancellationRequested();
			_cts = new CancellationTokenSource();
		}

		try
		{
			await _transport.StartAsync(ct).ConfigureAwait(false);

			lock (_lifecycleLock)
			{
				// ReSharper disable once MethodSupportsCancellation
				_readerTask = Task.Run(ReadLoopAsync);
			}

			try
			{
				await UciInitAsync(ct).ConfigureAwait(false);
				SetActivity(EngineActivity.Idle);
			}
			catch
			{
				try
				{
					await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
				}
				catch
				{
					/* best-effort cleanup */
				}

				throw;
			}
		}
		catch
		{
			lock (_lifecycleLock)
			{
				_cts?.Dispose();
				_cts        = null;
				_readerTask = null;
			}

			throw;
		}
	}

	/// <summary>
	///     Gracefully stops the engine, read loop, and disposes transports.
	/// </summary>
	public async Task StopAsync(CancellationToken ct = default)
	{
		await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
		try { await StopCoreAsync(ct).ConfigureAwait(false); }
		finally { _lifecycleGate.Release(); }
	}

	private async Task StopCoreAsync(CancellationToken ct)
	{
		CancellationTokenSource? ctsToCancel       = null;
		Task?                    readerTaskToAwait = null;

		lock (_lifecycleLock)
		{
			if (_cts is null) return; // Already stopped

			ctsToCancel       = _cts;
			readerTaskToAwait = _readerTask;
			_cts              = null;
			_readerTask       = null;
		}

		try
		{
			ctsToCancel.Cancel();
			if (readerTaskToAwait is { })
				try
				{
					await readerTaskToAwait.ConfigureAwait(false);
				}
				catch
				{
					/* best-effort */
				}
		}
		finally
		{
			ctsToCancel.Dispose();
		}

		await _transport.StopAsync(ct).ConfigureAwait(false);
		SetActivity(EngineActivity.Idle);
	}

	/// <summary>
	///     Sends the "stop" command and waits for bestmove before allowing another search.
	/// </summary>
	public Task StopSearchAsync(CancellationToken ct) => Searches.StopSearchAsync(ct);

	/// <summary>
	///     Sends "uci" to the engine and waits for "uciok" and "readyok" to confirm supported handshaking.
	/// </summary>
	public async Task UciInitAsync(CancellationToken ct)
	{
        ThrowIfDisposed();
		ResetHandshakeMetadata();
		await _commandModule.UciInitAsync(ct).ConfigureAwait(false);
		PromoteStandardCapabilities();
	}

	/// <summary>
	///     Informs the engine of a new game context via "ucinewgame"; calls <see cref="IsReadyAsync" />.
	/// </summary>
	public Task UciNewGameAsync(CancellationToken ct) => _commandModule.UciNewGameAsync(ct);

	/// <summary>
	///     Requests the current engine FEN using the non-standard <c>d</c> command and parses
	///     <c>fen ...</c> and <c>checkers ...</c> output.
	/// </summary>
	public Task<Fen?> TryGetFenViaDisplayBoardAsync(CancellationToken ct) =>
		_commandModule.TryGetFenViaDisplayBoardAsync(ct);

	/// <summary>
	///     Issues the non-standard <c>go perft 1</c> command and harvests all legal moves listed in the output.
	///     Waits for "readyok" for completion.
	/// </summary>
	public Task<ImmutableArray<string>> GetLegalMovesViaPerftAsync(CancellationToken ct) =>
		_commandModule.GetLegalMovesViaPerftAsync(ct);

	/// <summary>
	///     Runs a search with the supplied parameters and returns a <see cref="SearchResult" /> from "bestmove" and info
	///     lines.
	///     Applies a derived timeout based on input <paramref name="parameters" />.
	/// </summary>
	public Task<SearchResult> GoAsync(SearchParameters parameters, CancellationToken ct) =>
		Searches.ExecuteSearchAsync(parameters, ct);

	/// <summary>
	///     Disposes the engine client, releasing and stopping underlying transports.
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		await _lifecycleGate.WaitAsync().ConfigureAwait(false);
		try
		{
			if (_disposed) return;
            Volatile.Write(ref _disposed, true);
			await DisposeCoreAsync().ConfigureAwait(false);
		}
		finally { _lifecycleGate.Release(); }
	}

	private async ValueTask DisposeCoreAsync()
	{
		_transport.Error -= OnTransportError;
		_transport.StderrReceived -= OnTransportStderr;

		try
		{
			await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
		}
		catch
		{
			/* best-effort */
		}

		await _transport.DisposeAsync();
	}

    private UciSearchCoordinator Searches
    {
        get { ThrowIfDisposed(); return _searchCoordinator; }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed)) throw new ObjectDisposedException(nameof(UciEngineClient));
    }

	internal void SetExtensionCapabilities(
		UciCapabilityState displayBoardFen,
		UciCapabilityState perftMoveListing)
	{
		lock (_metadataLock)
		{
			_capabilities = _capabilities with
			{
				DisplayBoardFen = displayBoardFen,
				PerftMoveListing = perftMoveListing
			};
		}
	}

	IDisposable IUciLineSource.Subscribe(Action<string> handler)
	{
		if (handler is null) throw new ArgumentNullException(nameof(handler));

		RawLineReceived += handler;
		return new EventSubscription(() => RawLineReceived -= handler);
	}

	/// <summary>
	///     Main receive/read loop. Reads transport output lines, dispatches output events, parses known output, and
	///     manages generic waiters and search session events.
	/// </summary>
	private async Task ReadLoopAsync()
	{
		CancellationToken token;
		lock (_lifecycleLock)
		{
			if (_cts is null)
			{
				// Client was stopped before this task started - exit immediately
				_outputDispatcher.OnShutdown();
				SetActivity(EngineActivity.Idle);
				return;
			}

			token = _cts.Token;
		}

		try
		{
			await foreach (string line in _transport.ReadLinesAsync(token).ConfigureAwait(false))
			{
				PublishRawLineSafe(line);

				try
				{
					_outputDispatcher.Process(line);
				}
				catch (Exception ex)
				{
					ReportInternalError(ex);
				}
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			// Graceful shutdown.
		}
		catch (Exception ex)
		{
			ReportInternalError(ex);
		}
		finally
		{
			_outputDispatcher.OnShutdown();
			SetActivity(EngineActivity.Idle);
		}
	}

	private void ObserveProtocolMessage(UciProtocolMessage message)
	{
		PublishProtocolMessageSafe(message);

		switch (message.Type)
		{
			case UciProtocolMessageType.Id when message.Id is { Kind: UciIdKind.Name, Value: var name }:
				lock (_metadataLock)
				{
					_engineInfo = _engineInfo with { Name = name };
				}

				return;
			case UciProtocolMessageType.Id when message.Id is { Kind: UciIdKind.Author, Value: var author }:
				lock (_metadataLock)
				{
					_engineInfo = _engineInfo with { Author = author };
				}

				return;
			case UciProtocolMessageType.Option when message.Option is { Option: var option }:
				lock (_metadataLock)
				{
					var builder = _availableOptions.ToBuilder();
					int index   = -1;
					for (var i = 0; i < builder.Count; i++)
					{
						if (!string.Equals(builder[i].Name, option.Name, StringComparison.OrdinalIgnoreCase)) continue;

						index = i;
						break;
					}

					if (index >= 0)
						builder[index] = option;
					else
						builder.Add(option);

					_availableOptions = builder.ToImmutable();
					PromoteStandardCapabilities_NoLock();
				}

				return;
			case UciProtocolMessageType.Info when message.Info is { } infoMessage:
				PublishInfoReceivedSafe(infoMessage);
				if (infoMessage.Payload.PrincipalVariation is { } pv)
					PublishInfoPvSafe(pv);
				return;
			case UciProtocolMessageType.BestMove when message.BestMove is { } bestMoveMessage:
				PublishBestMoveMessageSafe(bestMoveMessage);
				PublishBestMoveSafe(bestMoveMessage.BestMove, bestMoveMessage.PonderMove);
				return;
		}
	}

	private void OnTransportError(Exception ex) => ReportInternalError(ex);

	private void OnTransportStderr(string line)
	{
		try
		{
			StderrReceived?.Invoke(line);
		}
		catch
		{
			// External subscribers must not interfere with transport processing.
		}
	}

	private void PromoteStandardCapabilities()
	{
		lock (_metadataLock)
		{
			PromoteStandardCapabilities_NoLock();
		}
	}

	private void PromoteStandardCapabilities_NoLock()
	{
		bool supportsPonder = _availableOptions.Any(static option =>
														string.Equals(
															option.Name, "Ponder", StringComparison.OrdinalIgnoreCase
														)
		);

		_capabilities = _capabilities with
		{
			DebugCommand = UciCapabilityState.Supported,
			RegisterCommand = UciCapabilityState.Supported,
			PonderHit = supportsPonder ? UciCapabilityState.Supported : UciCapabilityState.Unknown
		};
	}

	private void PublishBestMoveSafe(string bestMove, string ponderMove)
	{
		try
		{
			BestMoveReceived?.Invoke(bestMove, ponderMove);
		}
		catch
		{
			// External subscribers must not interfere with search completion.
		}
	}

	private void PublishBestMoveMessageSafe(UciBestMoveMessage message)
	{
		try
		{
			BestMoveMessageReceived?.Invoke(message);
		}
		catch
		{
			// External subscribers must not interfere with search completion.
		}
	}

	private void PublishInfoReceivedSafe(UciInfoMessage message)
	{
		try
		{
			InfoReceived?.Invoke(message);
		}
		catch
		{
			// External subscribers must not interfere with info processing.
		}
	}

	private void PublishInfoPvSafe(PrincipalVariation pv)
	{
		try
		{
			InfoPvReceived?.Invoke(pv);
		}
		catch
		{
			// External subscribers must not interfere with PV capture.
		}
	}

	private void PublishProtocolMessageSafe(UciProtocolMessage message)
	{
		try
		{
			ProtocolMessageReceived?.Invoke(message);
		}
		catch
		{
			// External subscribers must not interfere with protocol handling.
		}
	}

	private void PublishRawLineSafe(string line)
	{
		try
		{
			RawLineReceived?.Invoke(line);
		}
		catch
		{
			// External subscribers must not interfere with protocol handling.
		}

		try
		{
			LineReceived?.Invoke(line);
		}
		catch
		{
			// External subscribers must not interfere with protocol handling.
		}
	}

	private void ReportInternalError(Exception ex)
	{
		try
		{
			Error?.Invoke(ex);
		}
		catch
		{
			// External subscribers must not interfere with client teardown or protocol handling.
		}
	}

	private void ResetHandshakeMetadata()
	{
		lock (_metadataLock)
		{
			_engineInfo       = UciEngineInfo.Empty;
			_availableOptions = ImmutableArray<UciEngineOption>.Empty;
			_capabilities     = UciEngineCapabilities.Unknown;
		}
	}

	private bool TryGetStrengthOptions(out UciEngineOption limitStrengthOption, out UciEngineOption eloOption)
	{
		if (!TryGetOption(UciConstants.Options.LIMIT_STRENGTH, out limitStrengthOption) ||
			limitStrengthOption.Type != UciOptionType.Check)
		{
			eloOption = default;
			return false;
		}

		if (!TryGetOption(UciConstants.Options.ELO, out eloOption) ||
			eloOption.Type != UciOptionType.Spin)
		{
			limitStrengthOption = default;
			return false;
		}

		return true;
	}

	/// <summary>
	///     Atomically sets the engine activity state and publishes activity change notifications, if the state changed.
	/// </summary>
	private void SetActivity(EngineActivity next)
	{
		_activityTracker.Set(next);
	}

	private sealed class EventSubscription(Action unsubscribe) : IDisposable
	{
		private readonly Action _unsubscribe = unsubscribe ?? throw new ArgumentNullException(nameof(unsubscribe));
		private          int    _disposed;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

			try
			{
				_unsubscribe();
			}
			catch
			{
				/* swallow */
			}
		}
	}
}

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Bezoro.Chess.UCI.API.Common.Enums;
using Bezoro.Chess.UCI.API.Types;
using Bezoro.Chess.UCI.Internal;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.Chess.UCI.Protocol.Internal;

namespace Bezoro.Chess.UCI.API;

/// <summary>
///     Coordinates multiple UCI engines for fast information, pondering, and full move classification.
///     Orchestrates updating positions, synchronized pondering, background classification, and emits unified events
///     updating UI or consumers.
/// </summary>
public sealed class UciGameEngineSession : IAsyncDisposable, IDisposable
{
	private readonly GameEngineClients? _engineClients;
	private readonly CoordinatorClassificationRuntime _classificationRuntime;
	private readonly PlayableMatchClaimableDrawPolicy _claimableDrawPolicy;
	private readonly PlayableMatchControlledMoveFallbackPolicy _controlledMoveFallbackPolicy;
	private readonly PlayableMatchDrawOfferPolicy _drawOfferPolicy;
	private readonly GameEngineEventDispatcher        _events;
	private readonly MatchSideControllerKind          _blackController;
	private readonly List<PlayableMatchClockCheckpoint> _clockHistory = [];
	private int _clockHistoryOriginMoveCount;
	private readonly List<Task> _analysisTasks = [];
	private Task _ponderOperation = Task.CompletedTask;
	private readonly char                             _perspectiveColor;
	private readonly object                           _sync = new();
	private readonly SemaphoreSlim _commandGate = new(1, 1);
	private event Action<UciState>? StateObserved;
	private event Action? StreamsStopped;
	private readonly PlayableMatchTimeControl?        _timeControl;
	private readonly Func<DateTimeOffset>              _utcNowProvider;
	private readonly MatchSideControllerKind          _whiteController;

	private readonly UciCoordinatorOptions _options;

	private int _disposed;
    private TaskCompletionSource<bool>? _disposeCompletion;
	private CancellationTokenSource? _bestCts;
	private CancellationTokenSource? _classificationCts;
	private int                      _acceptedPonderGeneration;
	private Guid                     _gameId = Guid.NewGuid();
	private long                     _nextMoveId;
	private long                     _nextPendingPromotionId;
	private ImmutableList<GameMoveEvent> _appliedMoveHistory = [];
	private PlayableMatchResult      _forcedResult;
	private string?                 _declaredDrawMove;
	private PlayableMatchResult?     _claimableResult;
	private char?                    _drawOfferedBy;
	private PlayableMatchResult      _lastResult;
	private CoordinatorPendingPromotionRequest? _pendingPromotion;

	private UciState _state = UciState.Default;
	private long _stateVersion;

	private GameEngineClients Engines => _engineClients ?? throw new InvalidOperationException(
		"This local session has no engine configured. Use the constructor accepting an engine path for external analysis.");

	/// <summary>
	///     Raised when an error occurs in any of the underlying engines.
	/// </summary>
	public event Action<Exception>? Error;

	/// <summary>
	///     Raised when the coordinator is fully initialized and ready.
	/// </summary>
	public event Action? Ready;

	/// <summary>
	///     Raised when a new gameplay session starts.
	/// </summary>
	public event Action<GameStartedEvent>? GameStarted;

	/// <summary>
	///     Raised when the coordinator state changes.
	/// </summary>
	public event Action<UciState>? StateChanged;

	/// <summary>
	///     Raised when the visible game position snapshot changes, including legal moves and terminal-state transitions.
	/// </summary>
	public event Action<UciState>? PositionChanged;

	/// <summary>
	///     Raised when a full position is loaded directly into the coordinator.
	/// </summary>
	public event Action<PositionLoadedEvent>? PositionLoaded;

	/// <summary>
	///     Raised when the legal move set for the visible position changes.
	/// </summary>
	public event Action<UciState>? LegalMovesUpdated;

	/// <summary>
	///     Raised when the search lifecycle toggles between searching and idle.
	/// </summary>
	public event Action<UciState>? SearchStateChanged;

	/// <summary>
	///     Raised when the ponder engine publishes a new principal variation.
	/// </summary>
	public event Action<PrincipalVariation>? EvaluationChanged;

	/// <summary>
	///     Obsolete alias for <see cref="EvaluationChanged"/>.
	/// </summary>
	[Obsolete("Use EvaluationChanged or EventPublished instead.")]
	public event Action<PrincipalVariation>? EvaluationUpdated;

	/// <summary>
	///     Raised when the ponder engine produces a new best-move pair for the current search.
	/// </summary>
	public event Action<UciState>? BestMoveChanged;

	/// <summary>
	///     Raised for each move classification produced for the current position.
	/// </summary>
	public event Action<Move>? MoveClassified;

	/// <summary>
	///     Obsolete alias for <see cref="MoveClassified"/>.
	/// </summary>
	[Obsolete("Use MoveClassified or EventPublished instead.")]
	public event Action<Move>? MoveClassificationUpdated;

	/// <summary>
	///     Raised when all legal moves for the current position have been classified.
	/// </summary>
	public event Action<UciState>? ClassificationCompleted;

	/// <summary>
	///     Raised when the visible match reaches a terminal result.
	/// </summary>
	public event Action<UciState>? GameOver;

	/// <summary>Raised with the authoritative match identity and terminal state, including across queued delivery.</summary>
	public event Action<GameFinishedEvent>? GameFinished;

	/// <summary>
	///     Raised when the match result changes.
	/// </summary>
	public event Action<UciState>? ResultChanged;

	/// <summary>
	///     Raised when a move is fully applied.
	/// </summary>
	public event Action<GameMoveEvent>? MoveMade;

	/// <summary>
	///     Raised when an applied move captures a piece.
	/// </summary>
	public event Action<GameMoveEvent>? CaptureMade;

	/// <summary>
	///     Raised when an applied move castles.
	/// </summary>
	public event Action<GameMoveEvent>? CastlingMade;

	/// <summary>
	///     Raised when an applied move captures en passant.
	/// </summary>
	public event Action<GameMoveEvent>? EnPassantMade;

	/// <summary>
	///     Raised when a move requires a promotion choice before it can be applied.
	/// </summary>
	public event Action<PromotionRequiredEvent>? PromotionRequired;

	/// <summary>
	///     Raised when a pending promotion choice is resolved.
	/// </summary>
	public event Action<PromotionChosenEvent>? PromotionChosen;

	/// <summary>Raised when a matching pending promotion is canceled before a move is applied.</summary>
	public event Action<PromotionCanceledEvent>? PromotionCanceled;

	/// <summary>
	///     Raised when an applied move gives check.
	/// </summary>
	public event Action<GameMoveEvent>? Check;

	/// <summary>
	///     Raised when an applied move checkmates the opposing side.
	/// </summary>
	public event Action<GameMoveEvent>? Checkmate;

	/// <summary>
	///     Raised when an applied move stalemates the opposing side.
	/// </summary>
	public event Action<GameMoveEvent>? Stalemated;

	/// <summary>
	///     Raised when the side to move changes.
	/// </summary>
	public event Action<TurnChangedEvent>? TurnChanged;

	/// <summary>
	///     Raised when a move application is rejected.
	/// </summary>
	public event Action<IllegalMoveRejectedEvent>? IllegalMoveRejected;

	/// <summary>
	///     Raised when a draw offer is published.
	/// </summary>
	public event Action<UciState>? DrawOffered;

	/// <summary>
	///     Raised when a draw offer is declined or cleared explicitly.
	/// </summary>
	public event Action<UciState>? DrawDeclined;

	/// <summary>
	///     Raised when the match clock is paused.
	/// </summary>
	public event Action<UciState>? ClockPaused;

	/// <summary>
	///     Raised when the match clock is resumed.
	/// </summary>
	public event Action<UciState>? ClockResumed;

	/// <summary>
	///     Raised when one or more moves are undone.
	/// </summary>
	public event Action<MoveUndoneEvent>? MoveUndone;

	/// <summary>
	///     Raised when the engine starts thinking for the current position.
	/// </summary>
	public event Action<UciState>? EngineThinkingStarted;

	/// <summary>
	///     Raised when the engine stops thinking for the current position.
	/// </summary>
	public event Action<UciState>? EngineThinkingStopped;

	/// <summary>
	///     Obsolete alias for <see cref="Error"/>.
	/// </summary>
	[Obsolete("Use Error or EventPublished instead.")]
	public event Action<Exception>? EngineError;

	/// <summary>
	///     Raised when the coordinator has fully stopped.
	/// </summary>
	public event Action? Stopped;

	/// <summary>
	///     Raised for every canonical session event through a compact typed stream.
	/// </summary>
	public event Action<UciGameEngineSessionEvent>? EventPublished;

	/// <summary>
	///     Creates a local playable session. Loading positions, moving, promotion, and undo need no engine process.
	///     Use the constructor accepting an engine path when external analysis is needed.
	/// </summary>
	/// <param name="syncContext">Optional context for game-facing events.</param>
	/// <param name="options">Clock, match policies, and opt-in background analysis settings.</param>
	/// <param name="perspectiveColor">Board perspective, either w or b.</param>
	/// <param name="utcNowProvider">Optional clock source. A fixed UTC origin plus monotonic elapsed time avoids wall-clock adjustments.</param>
	public UciGameEngineSession(SynchronizationContext? syncContext = null, UciCoordinatorOptions? options = null,
		char perspectiveColor = 'w', Func<DateTimeOffset>? utcNowProvider = null)
		: this(null, syncContext, options, perspectiveColor, MatchSideControllerKind.Manual, MatchSideControllerKind.Manual, utcNowProvider)
	{
		_state = UciState.Default with { LegalMoves = Fen.Default.GetLegalMoves().ToImmutableList() };
		Interlocked.Increment(ref _stateVersion);
		InitializeClocks('w');
		ApplyMatchMetadata(_state);
	}

	/// <summary>
	///     Constructs a new UciGameEngineSession with engines initialized for the given enginePath, arguments, and working
	///     directory.
	/// </summary>
	/// <param name="enginePath">Path to the UCI engine executable.</param>
	/// <param name="args">Optional arguments for the engine.</param>
	/// <param name="workingDirectory">Optional working directory.</param>
	/// <param name="syncContext">Optional synchronization context to marshal events to (e.g. UI thread).</param>
	/// <param name="options">Optional configuration options for the coordinator.</param>
	/// <param name="perspectiveColor">The board perspective color, either <c>w</c> or <c>b</c>.</param>
	/// <param name="whiteController">Controller kind for the white side.</param>
	/// <param name="blackController">Controller kind for the black side.</param>
	/// <param name="utcNowProvider">Optional clock source. A fixed UTC origin plus monotonic elapsed time avoids wall-clock adjustments.</param>
	public UciGameEngineSession(
		string                  enginePath,
		IEnumerable<string>?    args             = null,
		string?                 workingDirectory = null,
		SynchronizationContext? syncContext      = null,
		UciCoordinatorOptions?  options          = null,
		char                    perspectiveColor = 'w',
		MatchSideControllerKind whiteController  = MatchSideControllerKind.Manual,
		MatchSideControllerKind blackController  = MatchSideControllerKind.Manual,
		Func<DateTimeOffset>?    utcNowProvider   = null)
		: this(
			GameEngineClients.Create(enginePath, args, workingDirectory),
			syncContext,
			options,
			perspectiveColor,
			whiteController,
			blackController,
			utcNowProvider
		) { }

	internal UciGameEngineSession(
		UciEngineClient         snapshotClient,
		UciPonderRuntime        ponder,
		UciEngineClient         classificationClient,
		SynchronizationContext? syncContext = null,
		UciCoordinatorOptions?  options     = null,
		char                    perspectiveColor = 'w',
		MatchSideControllerKind whiteController  = MatchSideControllerKind.Manual,
		MatchSideControllerKind blackController  = MatchSideControllerKind.Manual,
		Func<DateTimeOffset>?    utcNowProvider   = null)
		: this(new GameEngineClients(snapshotClient, ponder, classificationClient), syncContext, options,
			perspectiveColor, whiteController, blackController, utcNowProvider) { }

	private UciGameEngineSession(
		GameEngineClients? engineClients,
		SynchronizationContext? syncContext,
		UciCoordinatorOptions? options,
		char perspectiveColor,
		MatchSideControllerKind whiteController,
		MatchSideControllerKind blackController,
		Func<DateTimeOffset>? utcNowProvider = null)
	{
		_events      = new(syncContext);
		_options     = options ?? UciCoordinatorOptions.Default;
		_options.Validate();
		if (whiteController is not (MatchSideControllerKind.Manual or MatchSideControllerKind.Engine))
			throw new ArgumentOutOfRangeException(nameof(whiteController));
		if (blackController is not (MatchSideControllerKind.Manual or MatchSideControllerKind.Engine))
			throw new ArgumentOutOfRangeException(nameof(blackController));
		_engineClients = engineClients;
		_classificationRuntime = new(_sync);
		_perspectiveColor      = NormalizeColor(perspectiveColor, nameof(perspectiveColor));
		_whiteController       = whiteController;
		_blackController       = blackController;
		_timeControl           = _options.TimeControl;
		_utcNowProvider        = utcNowProvider ?? (static () => DateTimeOffset.UtcNow);
		_claimableDrawPolicy   = _options.ClaimableDrawPolicy;
		_drawOfferPolicy       = _options.DrawOfferPolicy;
		_controlledMoveFallbackPolicy = _options.ControlledMoveFallbackPolicy;

		if (engineClients is { } clients)
		{
			clients.Ponder.InfoPvWithGeneration += OnPonderInfo;
			clients.Ponder.BestMoveWithGeneration += PonderOnBestMove;
		}
	}

	/// <summary>
	///     Gets whether the primary engine and every started optional worker are healthy and responsive.
	/// </summary>
	public bool IsHealthy => _engineClients?.IsHealthy ?? false;

	/// <summary>
	///     Gets whether the primary engine has been started. Optional workers start only when requested.
	/// </summary>
	public bool IsStarted => _engineClients?.IsStarted ?? false;

	/// <summary>Gets the current match identity synchronously, before any queued event callbacks are delivered.</summary>
	public Guid CurrentGameId
	{
		get { lock (_sync) return _gameId; }
	}

	/// <summary>
	///     Gets the current position FEN (after all played moves).
	/// </summary>
	public Fen CurrentFen => State.CurrentFen;

	/// <summary>
	///     Gets the list of legal moves in the current position.
	/// </summary>
	public IReadOnlyList<string> LegalMoves => State.LegalMoves;

	/// <summary>
	///     Gets the list of moves played from the starting FEN.
	/// </summary>
	public IReadOnlyList<string> PlayedMoves => State.PlayedMoves;

	/// <summary>
	///     Gets the current evaluation from the ponder engine.
	/// </summary>
	public PrincipalVariation? Evaluation => State.Evaluation;

	/// <summary>
	///     Gets the configuration options for this coordinator.
	/// </summary>
	public UciCoordinatorOptions Options => _options;

	/// <summary>
	///     Gets the engine metadata reported by the snapshot client instance.
	/// </summary>
	public UciEngineInfo EngineInfo => _engineClients?.Snapshot.EngineInfo ?? UciEngineInfo.Empty;

	/// <summary>
	///     Gets the options advertised by the snapshot client instance during handshake.
	/// </summary>
	public ImmutableArray<UciEngineOption> AvailableOptions => _engineClients?.Snapshot.AvailableOptions ?? ImmutableArray<UciEngineOption>.Empty;

	/// <summary>
	///     Gets the capability state detected for the configured engine.
	/// </summary>
	public UciEngineCapabilities Capabilities => _engineClients?.Snapshot.Capabilities ?? UciEngineCapabilities.Unknown;

	/// <summary>
	///     Gets the side used for board-orientation and player-relative evaluation helpers.
	/// </summary>
	public char PerspectiveColor => _perspectiveColor;

	/// <summary>
	///     Gets the configured controller for White.
	/// </summary>
	public MatchSideControllerKind WhiteController => _whiteController;

	/// <summary>
	///     Gets the configured controller for Black.
	/// </summary>
	public MatchSideControllerKind BlackController => _blackController;

	/// <summary>
	///     Gets the current state of the coordinator.
	/// </summary>
	public UciState State
	{
		get
		{
			lock (_sync)
			{
				return _state;
			}
		}
		private set
		{
			lock (_sync)
			{
				_state = value;
				Interlocked.Increment(ref _stateVersion);
			}

			RaiseStateEvent(StateChanged, UciGameEngineSessionEventKind.StateChanged, value);
		}
	}

	/// <summary>
	///     Creates and starts a new UciGameEngineSession with engines initialized and ready.
	/// </summary>
	/// <param name="enginePath">Path to the UCI engine executable.</param>
	/// <param name="options">Configuration options for the coordinator.</param>
	/// <param name="syncContext">Optional synchronization context to marshal events to (e.g. UI thread).</param>
	/// <param name="perspectiveColor">The board perspective color, either <c>w</c> or <c>b</c>.</param>
	/// <param name="whiteController">Controller kind for the white side.</param>
	/// <param name="blackController">Controller kind for the black side.</param>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>A fully initialized and started coordinator.</returns>
	public static async Task<UciGameEngineSession> CreateAsync(
		string                  enginePath,
		UciCoordinatorOptions?  options     = null,
		SynchronizationContext? syncContext = null,
		char                    perspectiveColor = 'w',
		MatchSideControllerKind whiteController  = MatchSideControllerKind.Manual,
		MatchSideControllerKind blackController  = MatchSideControllerKind.Manual,
		CancellationToken       ct          = default)
	{
		var coordinator = new UciGameEngineSession(
			enginePath,
			null,
			null,
			syncContext,
			options,
			perspectiveColor,
			whiteController,
			blackController
		);
		try
		{
			await coordinator.StartAsync(ct).ConfigureAwait(false);
			return coordinator;
		}
		catch
		{
			await coordinator.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	/// <summary>
	///     Streams classified moves for the current position.
	///     Yields already classified moves first, then waits for new ones.
	///     Completes when all moves for the current position are classified.
	/// </summary>
	public async IAsyncEnumerable<Move> StreamClassifiedMovesAsync(
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(UciGameEngineSession));
		var channel = Channel.CreateUnbounded<Move>();
		var yielded = new HashSet<string>(StringComparer.Ordinal);
		var yieldedGate = new object();
		UciState initialState = default;
		Task<IReadOnlyDictionary<string, Move>>? completionTask;

		void WriteMoves(IReadOnlyDictionary<string, Move> moves)
		{
			lock (yieldedGate)
			{
				foreach (var kvp in moves)
					if (yielded.Add(kvp.Key)) channel.Writer.TryWrite(kvp.Value);
			}
		}

		void OnStateChanged(UciState state)
		{
			if (state.BaseFen != initialState.BaseFen || !state.PlayedMoves.SequenceEqual(initialState.PlayedMoves))
			{
				channel.Writer.TryComplete();
				return;
			}
			WriteMoves(state.ClassifiedMoves);
			if (state.IsClassificationComplete) channel.Writer.TryComplete();
		}

		void OnStopped() => channel.Writer.TryComplete();
		lock (_sync)
		{
			if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(UciGameEngineSession));
			initialState = _state;
			completionTask = _classificationRuntime.CompletionTask;
			WriteMoves(initialState.ClassifiedMoves);
			StateObserved += OnStateChanged;
			StreamsStopped += OnStopped;
		}

		if (initialState.IsClassificationComplete) channel.Writer.TryComplete();
		if (completionTask is { })
			_ = completionTask.ContinueWith(task =>
			{
				if (task.Status == TaskStatus.RanToCompletion) WriteMoves(task.Result);
				channel.Writer.TryComplete(task.IsFaulted ? task.Exception?.InnerException ?? task.Exception : null);
			}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

		try
		{
			while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
				while (channel.Reader.TryRead(out var move)) yield return move;
		}
		finally
		{
			StateObserved -= OnStateChanged;
			StreamsStopped -= OnStopped;
			channel.Writer.TryComplete();
		}
	}

	/// <summary>
	///     Streams the latest state for rendering and status updates.
	///     Yields the current state immediately and buffers at most one subsequent snapshot.
	///     A slow consumer skips intermediate snapshots. Use EventPublished for gameplay event delivery.
	/// </summary>
	/// <param name="ct">Cancellation token.</param>
	public async IAsyncEnumerable<UciState> StreamStateAsync(
		[EnumeratorCancellation] CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(UciGameEngineSession));
		var channel = Channel.CreateBounded<UciState>(new BoundedChannelOptions(1)
		{
			FullMode = BoundedChannelFullMode.DropOldest,
			SingleReader = true,
			SingleWriter = false,
			AllowSynchronousContinuations = false
		});
		void OnStateChanged(UciState state) => channel.Writer.TryWrite(state);
		void OnStopped() => channel.Writer.TryComplete();
		UciState initialState;
		lock (_sync)
		{
			if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(UciGameEngineSession));
			initialState = _state;
			StateObserved += OnStateChanged;
			StreamsStopped += OnStopped;
		}
		try
		{
			yield return initialState;
			while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
				while (channel.Reader.TryRead(out var state)) yield return state;
		}
		finally
		{
			StateObserved -= OnStateChanged;
			StreamsStopped -= OnStopped;
			channel.Writer.TryComplete();
		}
	}

	/// <summary>
	///     Starts a locally playable game with the standard position and freshly initialized clocks.
	/// </summary>
	public Task NewGameAsync(CancellationToken ct = default) =>
		RunCommandAsync(() => NewGameCoreAsync(ct), ct);

	private Task NewGameCoreAsync(CancellationToken ct = default, bool publishGameStarted = true)
	{
		ct.ThrowIfCancellationRequested();
		InvalidatePonderState(clearTransientState: true);
		CoordinatorRuntimeUtilities.CancelAndDispose(ref _bestCts);
		ClearState();
		QueuePonderStop();
		ResetGameplaySession();
		InitializeClocks('w');
		var snapshot = ApplyMatchMetadata(UciState.Default with
		{
			LegalMoves = Fen.Default.GetLegalMoves().ToImmutableList()
		}).Snapshot;
		State = snapshot;
		PublishPositionChanged(snapshot);
		RaiseStateEvent(LegalMovesUpdated, UciGameEngineSessionEventKind.LegalMovesUpdated, snapshot);
		if (publishGameStarted)
		{
			var gameStarted = new GameStartedEvent(_gameId, Fen.Default, DateTimeOffset.UtcNow);
			RaiseTypedEvent(GameStarted, gameStarted, UciGameEngineSessionEvent.ForGameStarted(gameStarted));
		}
		return Task.CompletedTask;
	}

	/// <summary>
	///     Resets to the starting chess position.
	/// </summary>
	/// <param name="ct">Cancellation token.</param>
	public Task ResetAsync(CancellationToken ct = default) =>
		LoadMatchAsync(PlayableMatchSetup.Standard, ct);

	/// <summary>
	///     Sets a UCI option on the currently started engines. Workers started later retain their own defaults.
	/// </summary>
	/// <param name="name">The option name.</param>
	/// <param name="value">The option value, or null to use the engine's default.</param>
	/// <param name="ct">Cancellation token.</param>
	public Task SetOptionAsync(string name, string? value, CancellationToken ct = default) =>
		Task.WhenAll(
			Engines.Snapshot.SetOptionAsync(name, value, ct),
			Engines.Ponder.IsStarted ? Engines.Ponder.SetOptionAsync(name, value, ct) : Task.CompletedTask,
			Engines.Classifier.IsStarted ? Engines.Classifier.SetOptionAsync(name, value, ct) : Task.CompletedTask
		);

	/// <summary>Sets Elo-limited play on the playing engine without limiting analysis workers.</summary>
	/// <param name="elo">Desired Elo within the playing engine's advertised range.</param>
	/// <param name="ct">Cancellation token.</param>
	/// <exception cref="ArgumentOutOfRangeException">The requested Elo is outside the advertised range.</exception>
	/// <exception cref="NotSupportedException">The playing engine does not support standard Elo limiting.</exception>
	public Task SetStrengthLimitAsync(int elo, CancellationToken ct = default) =>
		RunCommandAsync(() => Engines.Snapshot.SetStrengthLimitAsync(elo, ct), ct);

	/// <summary>Disables standard strength limiting on the playing engine, leaving analysis workers unchanged.</summary>
	/// <param name="ct">Cancellation token.</param>
	public Task ClearStrengthLimitAsync(CancellationToken ct = default) =>
		RunCommandAsync(() => Engines.Snapshot.ClearStrengthLimitAsync(ct), ct);

	/// <summary>
	///     Sends the standard UCI <c>debug on/off</c> command to the currently started engines.
	/// </summary>
	public Task SetDebugAsync(bool enabled, CancellationToken ct = default) =>
		Task.WhenAll(
			Engines.Snapshot.SetDebugAsync(enabled, ct),
			Engines.Ponder.IsStarted ? Engines.Ponder.SetDebugAsync(enabled, ct) : Task.CompletedTask,
			Engines.Classifier.IsStarted ? Engines.Classifier.SetDebugAsync(enabled, ct) : Task.CompletedTask
		);

	/// <summary>
	///     Sends the standard UCI <c>register</c> command to the currently started engines.
	/// </summary>
	public Task RegisterAsync(UciRegistration registration, CancellationToken ct = default) =>
		Task.WhenAll(
			Engines.Snapshot.RegisterAsync(registration, ct),
			Engines.Ponder.IsStarted ? Engines.Ponder.RegisterAsync(registration, ct) : Task.CompletedTask,
			Engines.Classifier.IsStarted ? Engines.Classifier.RegisterAsync(registration, ct) : Task.CompletedTask
		);

	/// <summary>
	///     Sets the position from a FEN string.
	/// </summary>
	/// <param name="fen">The FEN string representing the position.</param>
	/// <param name="ct">Cancellation token.</param>
	/// <exception cref="ArgumentException">Thrown when the FEN string is invalid.</exception>
	public Task SetPositionAsync(string fen, CancellationToken ct = default)
	{
		var parsed = Fen.Parse(fen);
		if (!parsed.HasValue)
			throw new ArgumentException($"Invalid FEN string: '{fen}'", nameof(fen));

		return LoadMatchAsync(new(parsed.Value), ct);
	}

	/// <summary>
	///     Starts the primary engine. Automatic background analysis explicitly opts into starting both optional workers.
	/// </summary>
	public Task StartAsync(CancellationToken ct = default) =>
		RunCommandAsync(() => StartCoreAsync(ct), ct);

	private async Task StartCoreAsync(CancellationToken ct = default)
	{
		_ = Engines;
		if (IsStarted) return;
		var snapshotStarted   = false;
		var ponderStarted     = false;
		var classifierStarted = false;

		try
		{
			await Engines.Snapshot.StartAsync(ct).ConfigureAwait(false);
			snapshotStarted = true;

			if (_options.EnableBackgroundAnalysis)
			{
				await EnsurePonderStartedCoreAsync(ct).ConfigureAwait(false);
				ponderStarted = true;
				await Engines.Classifier.StartAsync(ct).ConfigureAwait(false);
				classifierStarted = true;
			}

			ClearState();
			if (State.LegalMoves.Count == 0 && !State.Result.IsTerminal)
			{
				await NewGameCoreAsync(ct, publishGameStarted: false).ConfigureAwait(false);
			}

			var gameStarted = new GameStartedEvent(_gameId, State.BaseFen, DateTimeOffset.UtcNow);
			RaiseTypedEvent(GameStarted, gameStarted, UciGameEngineSessionEvent.ForGameStarted(gameStarted));
			RaiseSessionEvent(Ready, UciGameEngineSessionEventKind.Ready);
		}
		catch
		{
			await RollbackStartAsync(snapshotStarted || Engines.Snapshot.IsStarted, ponderStarted || Engines.Ponder.IsStarted, classifierStarted || Engines.Classifier.IsStarted).ConfigureAwait(false);
			throw;
		}
	}

	/// <summary>
	///     Begins an infinite search from the current position. Cancels any previous search.
	/// </summary>
	/// <param name="ct">Cancellation token.</param>
	public Task StartSearchAsync(CancellationToken ct = default) =>
		RunCommandAsync(() =>
		{
			var state = State;
			return StartSearchCoreAsync(state.BaseFen, state.PlayedMoves, ct);
		}, ct);

	/// <summary>
	///     Begins an infinite search from the specified position. Cancels any previous search.
	/// </summary>
	/// <param name="fen">Position to search from.</param>
	/// <param name="playedMoves">Optional played moves to append to the FEN.</param>
	/// <param name="ct">Cancellation token.</param>
	public Task StartSearchAsync(Fen fen, IEnumerable<string>? playedMoves = null, CancellationToken ct = default) =>
		RunCommandAsync(() => StartSearchCoreAsync(fen, playedMoves, ct), ct);

	private async Task StartSearchCoreAsync(
		Fen                  fen,
		IEnumerable<string>? playedMoves = null,
		CancellationToken    ct          = default)
	{
		await _ponderOperation.ConfigureAwait(false);
		InvalidatePonderState(clearTransientState: true);

		var newCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var token  = newCts.Token;
		CoordinatorRuntimeUtilities.CancelAndDispose(ref _bestCts, newCts);

		try
		{
			await EnsurePonderStartedCoreAsync(token).ConfigureAwait(false);
			await Engines.Ponder.StartSearchAsync(fen, playedMoves, token).ConfigureAwait(false);
			AcceptPonderGeneration(Engines.Ponder.CurrentSearchGeneration);
			SetSearchingState(true);
		}
		catch
		{
			if (CoordinatorRuntimeUtilities.CancelAndDisposeIfCurrent(ref _bestCts, newCts))
				SetSearchingState(false);

			throw;
		}
	}

	// Called while the command gate is held, before any optional worker is used.
	private async Task EnsurePonderStartedCoreAsync(CancellationToken ct)
	{
		if (!IsStarted) throw new InvalidOperationException("Start the primary engine before requesting engine analysis.");
		if (Engines.Ponder.IsStarted) return;
		try
		{
			await Engines.Ponder.StartAsync(ct).ConfigureAwait(false);
			await Engines.Ponder.SetOptionAsync("Threads", _options.PonderThreads.ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
			await Engines.Ponder.SetOptionAsync("MultiPv", _options.MultiPv.ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
		}
		catch
		{
			try { await Engines.Ponder.StopAsync(CancellationToken.None).ConfigureAwait(false); }
			catch { /* Preserve the startup failure after best-effort worker cleanup. */ }
			throw;
		}
	}

	/// <summary>
	///     Stops any ongoing search and all engine operations. Resets cached state and cancels in-flight move classification.
	/// </summary>
	public Task StopAsync(CancellationToken ct = default) =>
		RunCommandAsync(() => StopCoreAsync(ct), CancellationToken.None);

	private async Task StopCoreAsync(CancellationToken ct = default)
	{
		var stopFailures = new List<Exception>();
		bool cancellationRequested = ct.IsCancellationRequested;
		ClearState();

		if (_engineClients is { })
		{
			cancellationRequested = await TryStopStepAsync(
				StopSearchCoreAsync,
				cancellationRequested,
				stopFailures,
				ct,
				reportErrors: false
			).ConfigureAwait(false);
			cancellationRequested = await TryStopStepAsync(
				Engines.Classifier.IsStarted ? Engines.Classifier.StopAsync : static _ => Task.CompletedTask,
				cancellationRequested,
				stopFailures,
				ct
			).ConfigureAwait(false);
			cancellationRequested = await TryStopStepAsync(
				token => Engines.Ponder.IsStarted ? Engines.Ponder.StopAsync(token) : Task.CompletedTask,
				cancellationRequested,
				stopFailures,
				ct
			).ConfigureAwait(false);
			cancellationRequested = await TryStopStepAsync(
				Engines.Snapshot.IsStarted ? Engines.Snapshot.StopAsync : static _ => Task.CompletedTask,
				cancellationRequested,
				stopFailures,
				ct
			).ConfigureAwait(false);
		}

		PruneCompletedAnalysisTasks();
		Interlocked.Exchange(ref _acceptedPonderGeneration, 0);
		State = UciState.Default;

		StreamsStopped?.Invoke();
		RaiseSessionEvent(Stopped, UciGameEngineSessionEventKind.Stopped);
		CoordinatorRuntimeUtilities.ThrowStopFailures(stopFailures, cancellationRequested, ct);
	}

	/// <summary>
	///     Stops any ongoing infinite ponder search.
	/// </summary>
	public Task StopSearchAsync(CancellationToken ct = default) =>
		RunCommandAsync(() => StopSearchCommandCoreAsync(ct), ct);

	private async Task StopSearchCommandCoreAsync(CancellationToken ct = default)
	{
		try
		{
			await StopSearchCoreAsync(ct).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// Expected when cancellation is requested
		}
		catch (Exception ex)
		{
			RaiseError(ex);
		}
	}

	/// <summary>
	///     Loads a complete authored or saved match setup into the session and returns the refreshed state.
	/// </summary>
	/// <param name="setup">Complete match setup to load.</param>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>The refreshed state after the setup is loaded.</returns>
	public Task<UciState> LoadMatchAsync(PlayableMatchSetup setup, CancellationToken ct = default) =>
		RunCommandAsync(() => LoadMatchCoreAsync(setup, ct), ct);

	private async Task<UciState> LoadMatchCoreAsync(PlayableMatchSetup setup, CancellationToken ct = default)
	{
		setup.Validate();
		return await UpdatePositionCoreAsync(
			setup.BaseFen,
			setup.PlayedMoves,
			currentFen => CreateClockRestore(currentFen, setup.Clock),
			ct
		).ConfigureAwait(false);
	}

	/// <summary>Captures the local game and every available clock checkpoint for undo after reloading.</summary>
	/// <param name="ct">Cancellation while waiting for the serialized command gate.</param>
	/// <returns>An immutable save compatible with both playable session APIs. Snapshot arrays are allocated only on this request.</returns>
	public Task<PlayableMatchSave> CaptureSaveAsync(CancellationToken ct = default) =>
		RunCommandAsync(() =>
		{
			ct.ThrowIfCancellationRequested();
			var state = UpdateMatchMetadata();
			var clocks = ImmutableArray.CreateBuilder<PlayableMatchClockRestore>(_clockHistory.Count);
			PlayableMatchClockSetup? currentClock = null;
			if (_timeControl.HasValue)
			{
				foreach (var checkpoint in _clockHistory)
				{
					var start = PlayableMatchClockKernel.SnapshotAtTurnStart(_timeControl.Value, checkpoint);
					clocks.Add(new(start.WhiteRemaining, start.BlackRemaining, start.ActiveColor, start.DelayRemaining,
						start.IsPaused, checkpoint.WhiteMovesCompleted, checkpoint.BlackMovesCompleted,
						start.ActiveStageIndex, start.SnapshotUtc));
				}
				var snapshot = state.Clock!.Value;
				var last = _clockHistory[^1];
				currentClock = PlayableMatchClockSetup.FromExactRestore(new(snapshot.WhiteRemaining, snapshot.BlackRemaining,
					snapshot.ActiveColor, snapshot.DelayRemaining, snapshot.IsPaused, last.WhiteMovesCompleted,
					last.BlackMovesCompleted, snapshot.ActiveStageIndex, snapshot.SnapshotUtc));
			}
			return Task.FromResult(new PlayableMatchSave(new(state.BaseFen, state.PlayedMoves, currentClock),
				_timeControl, clocks.MoveToImmutable(), _clockHistoryOriginMoveCount, state.Result, _drawOfferedBy,
				_pendingPromotion.HasValue ? _pendingPromotion.Value.From + _pendingPromotion.Value.To : null,
				_claimableDrawPolicy, _drawOfferPolicy, _declaredDrawMove));
		}, ct);

	/// <summary>Validates and restores a local save, retaining historical clocks for later takebacks.</summary>
	/// <param name="save">Save captured by either playable session API, using equal time-control and draw policies.</param>
	/// <param name="countTimeSinceSave">Whether a running clock includes time elapsed since saving. Defaults to resuming at load time.</param>
	/// <param name="ct">Cancellation before replacement leaves the current match in place.</param>
	/// <returns>The restored game snapshot. Engine processes and analysis caches are excluded from saves.</returns>
	public Task<UciState> LoadSaveAsync(PlayableMatchSave save, bool countTimeSinceSave = false, CancellationToken ct = default) =>
		RunCommandAsync(() =>
		{
			ct.ThrowIfCancellationRequested();
			var validated = PlayableMatchSaveValidator.ValidateAndResolve(save, _timeControl, _claimableDrawPolicy,
				_drawOfferPolicy, ct, countTimeSinceSave ? null : _utcNowProvider());
			return UpdatePositionCoreAsync(save.Setup.BaseFen, save.Setup.PlayedMoves, null, ct, new(save, validated));
		}, ct);

	/// <summary>
	///     Updates the locally validated board position and legal moves, then optionally schedules background analysis.
	///     Completes without waiting for an external engine.
	/// </summary>
	/// <param name="fen">Position FEN</param>
	/// <param name="playedMoves">Optional moves played after the FEN</param>
	/// <param name="ct">Cancellation token</param>
	[Obsolete("Use LoadMatchAsync(new PlayableMatchSetup(fen, playedMoves), ct) instead.")]
	public Task UpdatePositionAsync(
		Fen                  fen,
		IEnumerable<string>? playedMoves,
		CancellationToken    ct = default) =>
		RunCommandAsync(() => UpdatePositionCoreAsync(fen, playedMoves, null, ct), ct);

	/// <summary>
	///     Fully updates the engines to reflect a new board position and optionally restores the match clock snapshot.
	/// </summary>
	/// <param name="fen">Position FEN</param>
	/// <param name="playedMoves">Optional moves played after the FEN</param>
	/// <param name="clockRestore">Optional clock snapshot to restore after the position is loaded.</param>
	/// <param name="ct">Cancellation token</param>
	[Obsolete("Use LoadMatchAsync(new PlayableMatchSetup(fen, playedMoves, clockSetup), ct) instead. Use PlayableMatchClockSetup.FromExactRestore for exact clock snapshots.")]
	public Task UpdatePositionAsync(
		Fen                         fen,
		IEnumerable<string>?        playedMoves,
		PlayableMatchClockRestore? clockRestore,
		CancellationToken           ct = default) =>
		RunCommandAsync(() => UpdatePositionCoreAsync(
			fen,
			playedMoves,
			clockRestore.HasValue ? _ => clockRestore.Value : null,
			ct
		), ct);

	private async Task<UciState> UpdatePositionCoreAsync(
		Fen                                    fen,
		IEnumerable<string>?                   playedMoves,
		Func<Fen, PlayableMatchClockRestore?>? resolveClockRestore,
		CancellationToken                      ct,
		SavedMatchRestore?                      savedMatch = null)
	{
		ct.ThrowIfCancellationRequested();
		var movesList = playedMoves?.Select(static move => move.Trim().ToLowerInvariant()).ToList() ?? new List<string>();
		if (!savedMatch.HasValue)
			PlayableMatchHistoryValidator.ValidateAndResolve(new(fen, movesList), _claimableDrawPolicy, ct);
		var previousTurn = State.CurrentFen.ActiveColor;
		var history = BuildLoadedMoveHistory(fen, movesList, ct);
		var effectiveFen = history.IsEmpty ? fen : history[^1].ResultingFen;
		var clockRestore = resolveClockRestore?.Invoke(effectiveFen);
		if (clockRestore.HasValue)
		{
			if (!_timeControl.HasValue)
				throw new InvalidOperationException("Cannot restore clock state because this session has no time control.");
			_ = PlayableMatchClockKernel.Restore(_timeControl.Value, effectiveFen.ActiveColor, clockRestore.Value);
		}

		var snapshot = await LoadPositionSnapshotAsync(fen, movesList, ct, effectiveFen).ConfigureAwait(false);
		ResetGameplaySession();
		_appliedMoveHistory = history.Select(move => move with { GameId = _gameId }).ToImmutableList();
		_nextMoveId = history.Length;
		if (savedMatch.HasValue)
		{
			_clockHistory.Clear();
			_clockHistory.AddRange(savedMatch.Value.Validated.ClockHistory);
			_clockHistoryOriginMoveCount = savedMatch.Value.Save.ClockHistoryOriginMoveCount;
			_forcedResult = savedMatch.Value.Save.Result;
			_declaredDrawMove = savedMatch.Value.Save.DeclaredDrawMove;
			_drawOfferedBy = savedMatch.Value.Save.DrawOfferedBy;
		}
		else if (clockRestore.HasValue)
			RestoreClock(snapshot.CurrentFen.ActiveColor, clockRestore.Value);
		else
			InitializeClocks(snapshot.CurrentFen.ActiveColor);
		if (!savedMatch.HasValue)
			_clockHistoryOriginMoveCount = _clockHistory.Count > 0 ? movesList.Count : 0;

		PromotionRequiredEvent? restoredPromotion = null;
		if (savedMatch?.Save.PendingPromotionMove is { } prefix &&
			CoordinatorMoveFactory.TryCreatePromotionRequest(_gameId, ++_nextPendingPromotionId, snapshot, prefix,
				GameMoveActor.Human, out var promotionRequest))
		{
			_pendingPromotion = new(promotionRequest.PendingPromotionId, promotionRequest.Actor,
				promotionRequest.From, promotionRequest.To, promotionRequest.MovingPiece,
				promotionRequest.AllowedPromotionPieces, snapshot);
			restoredPromotion = promotionRequest;
		}

		var metadata = ApplyMatchMetadata(snapshot);
		snapshot = metadata.Snapshot;
		if (metadata.ResultChanged)
			RaiseStateEvent(ResultChanged, UciGameEngineSessionEventKind.ResultChanged, snapshot);

		var positionLoaded = new PositionLoadedEvent(
			_gameId,
			snapshot.BaseFen,
			snapshot.CurrentFen,
			[.. snapshot.PlayedMoves],
			DateTimeOffset.UtcNow
		);
		RaiseTypedEvent(PositionLoaded, positionLoaded, UciGameEngineSessionEvent.ForPositionLoaded(positionLoaded));
		PublishPositionChanged(snapshot);
		RaiseStateEvent(LegalMovesUpdated, UciGameEngineSessionEventKind.LegalMovesUpdated, snapshot);
		if (previousTurn != snapshot.CurrentFen.ActiveColor)
		{
			var turnChanged = new TurnChangedEvent(
				_gameId,
				previousTurn,
				snapshot.CurrentFen.ActiveColor,
				snapshot.CurrentFen,
				DateTimeOffset.UtcNow
			);
			RaiseTypedEvent(TurnChanged, turnChanged, UciGameEngineSessionEvent.ForTurnChanged(turnChanged));
		}
		PublishGameOver(snapshot);
		if (restoredPromotion.HasValue && _pendingPromotion.HasValue)
			RaiseTypedEvent(PromotionRequired, restoredPromotion.Value,
				UciGameEngineSessionEvent.ForPromotionRequired(restoredPromotion.Value));

		await StartBackgroundWorkAsync(fen, movesList, snapshot.CurrentFen, snapshot.LegalMoves, ct).ConfigureAwait(false);
		return snapshot;
	}

	/// <summary>
	///     Gets the locally validated current FEN after all played moves, without contacting an engine.
	/// </summary>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>The current position FEN, or null if not available.</returns>
	public Task<Fen?> GetCurrentFenAsync(CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		return Task.FromResult<Fen?>(CurrentFen);
	}

	/// <summary>
	///     Waits until all legal moves in the current position have been classified.
	/// </summary>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>Dictionary of all classified moves.</returns>
	public async Task<IReadOnlyDictionary<string, Move>> WaitForClassificationAsync(CancellationToken ct = default)
	{
		Task<IReadOnlyDictionary<string, Move>> waitTask;
		lock (_sync)
		{
			if (_state.IsClassificationComplete)
				return _state.ClassifiedMoves;

			waitTask = _classificationRuntime.CompletionTask ??
					   Task.FromResult<IReadOnlyDictionary<string, Move>>(_state.ClassifiedMoves);
		}

		if (!ct.CanBeCanceled)
			return await waitTask.ConfigureAwait(false);

		var cancelTcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = ct.Register(static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null), cancelTcs);

		var completed = await Task.WhenAny(waitTask, cancelTcs.Task).ConfigureAwait(false);
		if (completed == cancelTcs.Task)
			throw new OperationCanceledException(ct);

		return await waitTask.ConfigureAwait(false);
	}

	/// <summary>
	///     Classifies a single move in the current position.
	/// </summary>
	/// <param name="move">The move to classify (in UCI notation).</param>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>The classified move with analysis.</returns>
	/// <exception cref="ArgumentException">Thrown when the move is not legal.</exception>
	public async Task<Move> ClassifyMoveAsync(string move, CancellationToken ct = default)
	{
		var currentState = State;

		return await ClassifyMoveForStateAsync(currentState, move, ct).ConfigureAwait(false);
	}

	internal async Task<Move> ClassifyMoveForStateAsync(UciState currentState, string move, CancellationToken ct = default)
	{
		if (!currentState.LegalMoves.Contains(move))
			throw new ArgumentException($"Move '{move}' is not legal in the current position.", nameof(move));

		// Check if already classified
		if (currentState.ClassifiedMoves.TryGetValue(move, out var existing))
			return existing;

		return await ClassifyMoveWithClassificationClientAsync(currentState.CurrentFen, move, ct).ConfigureAwait(false);
	}

	private static Task<Move> ClassifyMoveForEventAsync(
		UciState currentState, string move, CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		if (currentState.ClassifiedMoves.TryGetValue(move, out var classified)) return Task.FromResult(classified);
		if (CoordinatorMoveFactory.TryBuildFallbackMove(currentState.CurrentFen, move, out var localMove))
			return Task.FromResult(localMove);
		throw new InvalidOperationException("Unable to classify the legal move locally.");
	}

	/// <summary>
	///     Performs a blocking search with the specified parameters and returns the result.
	/// </summary>
	/// <param name="parameters">Search parameters (depth, time, nodes, etc.).</param>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>The search result containing the best move and evaluation.</returns>
	public Task<SearchResult> SearchAsync(SearchParameters parameters, CancellationToken ct = default)
	{
		var state = State;
		return Engines.Snapshot.SearchAsync(state.BaseFen, state.PlayedMoves, parameters, ct);
	}

	/// <summary>
	///     Applies a legal move using local rules without waiting for an external engine.
	///     This is a convenience wrapper around the position update API.
	/// </summary>
	/// <param name="move">The move to play (in UCI notation, e.g. "e2e4").</param>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>The new state after the move is applied.</returns>
	/// <exception cref="ArgumentException">Thrown when the move is not legal in the current position.</exception>
	public async Task<UciState> MakeMoveAsync(string move, CancellationToken ct = default)
	{
		return await MakeMoveAsync(move, GameMoveActor.Human, ct).ConfigureAwait(false);
	}

	/// <summary>
	///     Plays the current side automatically when that side is configured as engine-controlled.
	/// </summary>
	public Task<EngineMoveResult> PlayControlledMoveAsync(CancellationToken ct = default) =>
		RunCommandAsync(() => PlayControlledMoveCoreAsync(ct), ct);

	private async Task<EngineMoveResult> PlayControlledMoveCoreAsync(CancellationToken ct = default)
	{
		EnsureEngineMoveCanStart();
		if (GetController(CurrentFen.ActiveColor) != MatchSideControllerKind.Engine)
			throw new InvalidOperationException("The current side is not configured for engine control.");

		try
		{
			var result = await SearchAsync(new() { MoveTimeMs = _options.EngineMoveTimeMs }, ct).ConfigureAwait(false);
			await ApplyEngineReplyCoreAsync(result, ct).ConfigureAwait(false);
			return new(result.BestMove.ToLowerInvariant(), result);
		}
		catch (InvalidOperationException) when (
			_controlledMoveFallbackPolicy == PlayableMatchControlledMoveFallbackPolicy.UseLocalFallback &&
			!State.Result.IsTerminal &&
			TryResolveFallbackControlledMove(State, out var fallbackMove))
		{
			await MakeMoveCoreAsync(fallbackMove, GameMoveActor.Engine, ct).ConfigureAwait(false);
			return new(fallbackMove, default);
		}
	}

	/// <summary>Searches and applies one engine defense regardless of the current side's configured controller.</summary>
	/// <param name="ct">Cancellation token for the search and move application.</param>
	/// <returns>The state after exactly one legal engine move has been applied.</returns>
	/// <remarks>
	/// Uses the independent analysis worker and the configured engine-move time budget. Playing-strength limits do not
	/// weaken this search. A failed, canceled, incomplete, or illegal engine reply never falls back to a local move.
	/// </remarks>
	/// <exception cref="InvalidOperationException">The match cannot accept a move or the engine supplies no complete legal reply.</exception>
	public Task<UciState> PlayEngineDefenseAsync(CancellationToken ct = default) =>
		RunCommandAsync(async () =>
		{
			EnsureEngineMoveCanStart();
			if (!IsStarted) throw new InvalidOperationException("Start the primary engine before requesting engine defense.");
			if (!Engines.Classifier.IsStarted) await Engines.Classifier.StartAsync(ct).ConfigureAwait(false);
			await Engines.Classifier.ClearStrengthLimitAsync(ct).ConfigureAwait(false);
			var state = State;
			var reply = await Engines.Classifier.SearchAsync(state.BaseFen, state.PlayedMoves,
				new() { MoveTimeMs = _options.EngineMoveTimeMs }, ct).ConfigureAwait(false);
			return await ApplyEngineReplyCoreAsync(reply, ct).ConfigureAwait(false);
		}, ct);

	private void EnsureEngineMoveCanStart()
	{
		if (State.Result.IsTerminal) throw new InvalidOperationException("The match has already ended.");
		EnsureTurnHasTimeRemaining();
		if (_pendingPromotion.HasValue) throw new InvalidOperationException("Resolve the pending promotion before requesting an engine move.");
	}

	private async Task<UciState> ApplyEngineReplyCoreAsync(SearchResult reply, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		var before = State;
		string move = reply.BestMove?.Trim().ToLowerInvariant() ?? string.Empty;
		if (!before.LegalMoves.Contains(move))
			throw new InvalidOperationException("The engine did not return a complete legal move for the current position.");
		var after = await MakeMoveCoreAsync(move, GameMoveActor.Engine, ct).ConfigureAwait(false);
		if (after.PlayedMoves.Count != before.PlayedMoves.Count + 1 || after.PlayedMoves[^1] != move)
			throw new InvalidOperationException("The engine reply was not applied as exactly one move.");
		return after;
	}

	/// <summary>
	///     Plays the current side automatically when it is engine-controlled and the match is still playable.
	/// </summary>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>
	///     The engine move result when a controlled move was played; otherwise <see langword="null" /> when the current
	///     side is manually controlled or the match is terminal.
	/// </returns>
	public async Task<EngineMoveResult?> PlayControlledMoveIfNeededAsync(CancellationToken ct = default)
	{
		var state = State;
		if (state.Result.IsTerminal ||
			state.LegalMoves.Count == 0 ||
			GetController(state.CurrentFen.ActiveColor) != MatchSideControllerKind.Engine)
		{
			return null;
		}

		return await PlayControlledMoveAsync(ct).ConfigureAwait(false);
	}

	/// <summary>
	///     Applies a legal move locally with an explicit actor classification.
	/// </summary>
	/// <param name="move">The move to play (in UCI notation, e.g. "e2e4").</param>
	/// <param name="actor">Who initiated the move.</param>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>The new state after the move is applied.</returns>
	/// <exception cref="ArgumentException">Thrown when the move is not legal in the current position.</exception>
	public Task<UciState> MakeMoveAsync(string move, GameMoveActor actor, CancellationToken ct = default) =>
		RunCommandAsync(() => MakeMoveCoreAsync(move, actor, ct), ct);

	private async Task<UciState> MakeMoveCoreAsync(string move, GameMoveActor actor, CancellationToken ct = default)
	{
		UciState currentState;
		CoordinatorPendingPromotionRequest? pendingPromotion;
		lock (_sync)
		{
			currentState     = _state;
			pendingPromotion = _pendingPromotion;
		}

		ct.ThrowIfCancellationRequested();
		if (currentState.Result.IsTerminal)
			throw new InvalidOperationException("The match has already ended.");

		string normalizedMove = move?.Trim().ToLowerInvariant() ?? string.Empty;
		EnsureTurnHasTimeRemaining();

		if (pendingPromotion.HasValue)
		{
			RejectIllegalMove(
				normalizedMove,
				"A promotion choice is pending. Resolve it before applying another move.",
				currentState.LegalMoves,
				true
			);
		}

		long pendingPromotionId = Interlocked.Increment(ref _nextPendingPromotionId);

		if (CoordinatorMoveFactory.TryCreatePromotionRequest(
				_gameId,
				pendingPromotionId,
				currentState,
				normalizedMove,
				actor,
				out var request))
		{
			lock (_sync)
			{
				_pendingPromotion = new(
					request.PendingPromotionId,
					request.Actor,
					request.From,
					request.To,
					request.MovingPiece,
					request.AllowedPromotionPieces,
					currentState
				);
			}

			RaiseTypedEvent(
				PromotionRequired,
				request,
				UciGameEngineSessionEvent.ForPromotionRequired(request)
			);
			return currentState;
		}

		if (!currentState.LegalMoves.Contains(normalizedMove))
			RejectIllegalMove(
				normalizedMove,
				$"Move '{normalizedMove}' is not legal in the current position.",
				currentState.LegalMoves,
				false
			);

		var classifiedMove = await ClassifyMoveForEventAsync(currentState, normalizedMove, ct).ConfigureAwait(false);
		return await ApplyMoveAsync(currentState, normalizedMove, classifiedMove, actor, null, ct).ConfigureAwait(false);
	}

	/// <summary>
	///     Resolves a pending promotion choice and applies the completed move.
	/// </summary>
	/// <param name="pendingPromotionId">The identifier previously published in <see cref="PromotionRequired" />.</param>
	/// <param name="pieceType">The chosen promotion piece type.</param>
	/// <param name="ct">Cancellation token.</param>
	public Task<UciState> ChoosePromotionAsync(long pendingPromotionId, PieceType pieceType, CancellationToken ct = default) =>
		RunCommandAsync(() => ChoosePromotionCoreAsync(pendingPromotionId, pieceType, ct), ct);

	/// <summary>Cancels a matching promotion prompt without moving a piece or rewinding the clock.</summary>
	/// <param name="pendingPromotionId">Identifier from the promotion request. Stale requests cannot cancel a newer prompt.</param>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>True when a matching prompt was canceled; false when it is no longer pending.</returns>
	public Task<bool> CancelPendingPromotionAsync(long pendingPromotionId, CancellationToken ct = default) =>
		RunCommandAsync(() =>
		{
			ct.ThrowIfCancellationRequested();
			var state = UpdateMatchMetadata();
			lock (_sync)
			{
				if (!_pendingPromotion.HasValue || _pendingPromotion.Value.Id != pendingPromotionId)
					return Task.FromResult(false);
				_pendingPromotion = null;
			}
			var canceled = new PromotionCanceledEvent(_gameId, pendingPromotionId, state.CurrentFen, _utcNowProvider());
			RaiseTypedEvent(PromotionCanceled, canceled, UciGameEngineSessionEvent.ForPromotionCanceled(canceled));
			return Task.FromResult(true);
		}, ct);

	private async Task<UciState> ChoosePromotionCoreAsync(
		long             pendingPromotionId,
		PieceType        pieceType,
		CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		if (State.Result.IsTerminal)
			throw new InvalidOperationException("The match has already ended.");
		EnsureTurnHasTimeRemaining();
		CoordinatorPendingPromotionRequest pending;
		lock (_sync)
		{
			if (!_pendingPromotion.HasValue || _pendingPromotion.Value.Id != pendingPromotionId)
				throw new InvalidOperationException("No matching pending promotion request exists.");

			pending = _pendingPromotion.Value;
		}

		string? notation = CoordinatorMoveFactory.ResolvePromotionMoveNotation(pending, pieceType);
		if (notation is null)
			throw new ArgumentException($"Promotion piece '{pieceType}' is not allowed for the pending move.", nameof(pieceType));

		var promotionChosen = new PromotionChosenEvent(
			_gameId,
			pending.Id,
			notation,
			pieceType,
			DateTimeOffset.UtcNow
		);

		var classifiedMove = await ClassifyMoveForEventAsync(pending.State, notation, ct).ConfigureAwait(false);
		return await ApplyMoveAsync(
			pending.State,
			notation,
			classifiedMove,
			pending.Actor,
			promotionChosen,
			ct
		).ConfigureAwait(false);
	}

	/// <summary>
	///     Reverts one or more moves locally. Retained clock values, stages, and move counts are restored;
	///     the retained turn restarts unpaused at the undo time.
	/// </summary>
	/// <param name="count">Number of moves to undo. Defaults to 1.</param>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>The new state after undoing moves, or the current state if no moves were undone.</returns>
	public Task<UciState> UndoAsync(int count = 1, CancellationToken ct = default) =>
		RunCommandAsync(() => UndoCoreAsync(count, ct, static (state, _) => state), ct);

	/// <summary>
	///     Reverts moves and returns their authoritative piece-motion payloads with the resulting state.
	///     The result is available without waiting for synchronization-context event delivery.
	/// </summary>
	/// <remarks>
	///     Publishes the same events and restores the same clocks as <see cref="UndoAsync" />.
	///     Cancellation before the commit leaves history unchanged. Cancellation during optional background work
	///     may be reported after the undo has already committed.
	/// </remarks>
	/// <param name="count">Maximum number of plies to undo. Must be positive; defaults to one.</param>
	/// <param name="ct">Cancellation token, with the same commit semantics as <see cref="UndoAsync" />.</param>
	/// <returns>
	///     The completed undo's state and removed moves in chronological order. When history is empty,
	///     returns the current state and an empty move array without publishing a <see cref="MoveUndone" /> event.
	/// </returns>
	public Task<UciUndoResult> UndoWithDetailsAsync(int count = 1, CancellationToken ct = default) =>
		RunCommandAsync(() => UndoCoreAsync(count, ct, static (state, moves) => new UciUndoResult(state, moves)), ct);

	private async Task<TResult> UndoCoreAsync<TResult>(int count, CancellationToken ct,
		Func<UciState, ImmutableArray<GameMoveEvent>, TResult> createResult)
	{
		ct.ThrowIfCancellationRequested();
		if (count < 1)
			throw new ArgumentOutOfRangeException(nameof(count), "Count must be at least 1.");

		UciState currentState;
		ImmutableList<GameMoveEvent> appliedMoves;
		lock (_sync)
		{
			currentState = _state;
			appliedMoves = _appliedMoveHistory;
		}

		if (currentState.PlayedMoves.Count == 0)
			return createResult(currentState, ImmutableArray<GameMoveEvent>.Empty);

		int movesToRemove = Math.Min(count, currentState.PlayedMoves.Count);
		int retainedCount = currentState.PlayedMoves.Count - movesToRemove;
		if (_clockHistory.Count > 0 && retainedCount < _clockHistoryOriginMoveCount)
			throw new InvalidOperationException("Cannot undo beyond the available clock history.");
		var newMoves = currentState.PlayedMoves.RemoveRange(retainedCount, movesToRemove);
		var previousTurn = currentState.CurrentFen.ActiveColor;
		var retainedFen = retainedCount > 0 ? appliedMoves[retainedCount - 1].ResultingFen : currentState.BaseFen;
		var snapshot = await LoadPositionSnapshotAsync(currentState.BaseFen, newMoves, ct, retainedFen).ConfigureAwait(false);
		var removedMoves = appliedMoves.Skip(retainedCount).ToImmutableArray();

		lock (_sync)
		{
			_appliedMoveHistory = appliedMoves.RemoveRange(retainedCount, removedMoves.Length);
			_forcedResult = default;
			_declaredDrawMove = null;
			_drawOfferedBy = null;
			_pendingPromotion = null;
		}
		if (_clockHistory.Count > 0)
		{
			int checkpointIndex = retainedCount - _clockHistoryOriginMoveCount;
			_clockHistory.RemoveRange(checkpointIndex + 1, _clockHistory.Count - checkpointIndex - 1);
			_clockHistory[^1] = PlayableMatchClockKernel.RestartTurn(_clockHistory[^1], _utcNowProvider());
		}
		var metadata = ApplyMatchMetadata(snapshot);
		snapshot = metadata.Snapshot;

		var moveUndone = new MoveUndoneEvent(_gameId, removedMoves, snapshot.CurrentFen, DateTimeOffset.UtcNow);
		RaiseTypedEvent(MoveUndone, moveUndone, UciGameEngineSessionEvent.ForMoveUndone(moveUndone));
		if (metadata.ResultChanged)
			RaiseStateEvent(ResultChanged, UciGameEngineSessionEventKind.ResultChanged, snapshot);
		if (previousTurn != snapshot.CurrentFen.ActiveColor)
		{
			var turnChanged = new TurnChangedEvent(
				_gameId,
				previousTurn,
				snapshot.CurrentFen.ActiveColor,
				snapshot.CurrentFen,
				DateTimeOffset.UtcNow
			);
			RaiseTypedEvent(TurnChanged, turnChanged, UciGameEngineSessionEvent.ForTurnChanged(turnChanged));
		}
		PublishPositionChanged(snapshot);
		RaiseStateEvent(LegalMovesUpdated, UciGameEngineSessionEventKind.LegalMovesUpdated, snapshot);
		PublishGameOver(snapshot);

		await StartBackgroundWorkAsync(
			currentState.BaseFen,
			newMoves,
			snapshot.CurrentFen,
			snapshot.LegalMoves,
			ct
		).ConfigureAwait(false);
		return createResult(State, removedMoves);
	}

	/// <summary>
	///     Offers a draw from the current side.
	/// </summary>
	public Task<UciState> OfferDrawAsync(CancellationToken ct = default) =>
		RunCommandAsync(() => OfferDrawCoreAsync(ct), ct);

	/// <summary>Offers a draw on behalf of either side, including while the opponent is thinking.</summary>
	public Task<UciState> OfferDrawAsync(char offeringSide, CancellationToken ct = default) =>
		RunCommandAsync(() => OfferDrawCoreAsync(ct, offeringSide), ct);

	private Task<UciState> OfferDrawCoreAsync(CancellationToken ct = default, char? offeringSide = null)
	{
		ct.ThrowIfCancellationRequested();
		char side = NormalizeColor(offeringSide ?? State.CurrentFen.ActiveColor, nameof(offeringSide));
		var current = UpdateMatchMetadata();
		if (current.Result.IsTerminal)
			return Task.FromResult(current);
		if (_drawOfferedBy.HasValue && _drawOfferedBy != side)
			throw new InvalidOperationException("Respond to the opposing draw offer before making another offer.");
		var snapshot = UpdateMatchMetadata(drawOfferedBy: side, updateDrawOffer: true);
		RaiseStateEvent(DrawOffered, UciGameEngineSessionEventKind.DrawOffered, snapshot);
		return Task.FromResult(snapshot);
	}

	/// <summary>
	///     Accepts the current pending draw offer and ends the game as a draw.
	/// </summary>
	public Task<UciState> AcceptDrawAsync(CancellationToken ct = default) =>
		RunCommandAsync(() => AcceptDrawCoreAsync(ct), ct);

	/// <summary>Accepts a draw only when the supplied side is the recipient of the pending offer.</summary>
	public Task<UciState> AcceptDrawAsync(char acceptingSide, CancellationToken ct = default) =>
		RunCommandAsync(() => AcceptDrawCoreAsync(ct, acceptingSide), ct);

	private Task<UciState> AcceptDrawCoreAsync(CancellationToken ct = default, char? acceptingSide = null)
	{
		ct.ThrowIfCancellationRequested();
		var current = UpdateMatchMetadata();
		if (State.Result.IsTerminal)
			throw new InvalidOperationException("The match has already ended.");
		if (!_drawOfferedBy.HasValue)
			throw new InvalidOperationException("There is no pending draw offer.");
		if (acceptingSide.HasValue && NormalizeColor(acceptingSide.Value, nameof(acceptingSide)) == _drawOfferedBy)
			throw new InvalidOperationException("Only the recipient can accept a draw offer.");
		var snapshot = SetForcedResult(new(PlayableMatchResultReason.DrawAgreement, null), completedAtUtc: current.Clock?.SnapshotUtc);
		return Task.FromResult(snapshot);
	}

	/// <summary>
	///     Declines the current pending draw offer.
	/// </summary>
	public Task<UciState> DeclineDrawAsync(CancellationToken ct = default) =>
		RunCommandAsync(() => DeclineDrawCoreAsync(ct), ct);

	/// <summary>Declines a draw only when the supplied side is the recipient of the pending offer.</summary>
	public Task<UciState> DeclineDrawAsync(char decliningSide, CancellationToken ct = default) =>
		RunCommandAsync(() => DeclineDrawCoreAsync(ct, decliningSide), ct);

	private Task<UciState> DeclineDrawCoreAsync(CancellationToken ct = default, char? decliningSide = null)
	{
		ct.ThrowIfCancellationRequested();
		UpdateMatchMetadata();
		if (State.Result.IsTerminal)
			throw new InvalidOperationException("The match has already ended.");
		if (!_drawOfferedBy.HasValue)
			throw new InvalidOperationException("There is no pending draw offer.");
		if (decliningSide.HasValue && NormalizeColor(decliningSide.Value, nameof(decliningSide)) == _drawOfferedBy)
			throw new InvalidOperationException("Only the recipient can decline a draw offer.");
		var snapshot = UpdateMatchMetadata(drawOfferedBy: null, updateDrawOffer: true);
		RaiseStateEvent(DrawDeclined, UciGameEngineSessionEventKind.DrawDeclined, snapshot);
		return Task.FromResult(snapshot);
	}

	/// <summary>
	///     Claims the currently claimable draw result when available.
	/// </summary>
	public Task<UciState> ClaimDrawAsync(CancellationToken ct = default) =>
		RunCommandAsync(() => ClaimDrawCoreAsync(ct), ct);

	/// <summary>Claims a threefold or fifty-move draw by declaring a legal intended move without playing it.</summary>
	/// <param name="intendedMove">Legal move in UCI notation that would make the draw claim available.</param>
	/// <param name="ct">Cancellation token.</param>
	public Task<UciState> ClaimDrawAsync(string intendedMove, CancellationToken ct = default) =>
		RunCommandAsync(() =>
		{
			ct.ThrowIfCancellationRequested();
			var state = UpdateMatchMetadata();
			if (state.Result.IsTerminal)
				throw new InvalidOperationException("The match has already ended.");
			if (_pendingPromotion.HasValue)
				throw new InvalidOperationException("Resolve the pending promotion before declaring another move.");
			var next = state.CurrentFen.ApplyMove(intendedMove);
			var reason = next.HalfmoveClock >= 100 ? PlayableMatchResultReason.FiftyMoveRule
				: CountRepetitions(state.BaseFen, next) >= 2 ? PlayableMatchResultReason.ThreefoldRepetition
				: PlayableMatchResultReason.None;
			if (reason == PlayableMatchResultReason.None)
				throw new InvalidOperationException("The intended move does not establish a claimable draw.");
			return Task.FromResult(SetForcedResult(new(reason, null), LocalPositionRules.NormalizeMove(intendedMove), state.Clock?.SnapshotUtc));
		}, ct);

	private Task<UciState> ClaimDrawCoreAsync(CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		var current = UpdateMatchMetadata();
		if (!State.ClaimableResult.HasValue)
			throw new InvalidOperationException("There is no claimable draw available.");

		var snapshot = SetForcedResult(State.ClaimableResult.Value, completedAtUtc: current.Clock?.SnapshotUtc);
		return Task.FromResult(snapshot);
	}

	/// <summary>
	///     Resigns on behalf of the current side.
	/// </summary>
	public Task<UciState> ResignAsync(CancellationToken ct = default) =>
		RunCommandAsync(() => ResignCoreAsync(ct), ct);

	/// <summary>Resigns on behalf of either side, including while the opponent is thinking.</summary>
	public Task<UciState> ResignAsync(char resigningSide, CancellationToken ct = default) =>
		RunCommandAsync(() => ResignCoreAsync(ct, resigningSide), ct);

	private Task<UciState> ResignCoreAsync(CancellationToken ct = default, char? resigningSide = null)
	{
		ct.ThrowIfCancellationRequested();
		char side = NormalizeColor(resigningSide ?? CurrentFen.ActiveColor, nameof(resigningSide));
		var current = UpdateMatchMetadata();
		char opponent = Opposite(side);
		var snapshot = SetForcedResult(new(PlayableMatchResultReason.Resignation,
			LocalPositionRules.CanPossiblyMate(CurrentFen, opponent) ? opponent : null), completedAtUtc: current.Clock?.SnapshotUtc);
		return Task.FromResult(snapshot);
	}

	/// <summary>
	///     Pauses the active match clock when time control is enabled.
	/// </summary>
	public Task<UciState> PauseClockAsync(CancellationToken ct = default) =>
		RunCommandAsync(() => PauseClockCoreAsync(ct), ct);

	private Task<UciState> PauseClockCoreAsync(CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		if (!_timeControl.HasValue || _clockHistory.Count == 0 || _clockHistory[^1].PausedAtUtc.HasValue)
			return Task.FromResult(State);

		_clockHistory[^1] = PlayableMatchClockKernel.Pause(_clockHistory[^1], _utcNowProvider());
		var snapshot = UpdateMatchMetadata();
		RaiseStateEvent(ClockPaused, UciGameEngineSessionEventKind.ClockPaused, snapshot);
		return Task.FromResult(snapshot);
	}

	/// <summary>
	///     Resumes the active match clock when paused.
	/// </summary>
	public Task<UciState> ResumeClockAsync(CancellationToken ct = default) =>
		RunCommandAsync(() => ResumeClockCoreAsync(ct), ct);

	private Task<UciState> ResumeClockCoreAsync(CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		if (State.Result.IsTerminal || !_timeControl.HasValue || _clockHistory.Count == 0 || !_clockHistory[^1].PausedAtUtc.HasValue)
			return Task.FromResult(State);

		_clockHistory[^1] = PlayableMatchClockKernel.Resume(_clockHistory[^1], _utcNowProvider());
		var snapshot = UpdateMatchMetadata();
		RaiseStateEvent(ClockResumed, UciGameEngineSessionEventKind.ClockResumed, snapshot);
		return Task.FromResult(snapshot);
	}

	/// <summary>
	///     Returns the configured controller for the supplied side.
	/// </summary>
	public MatchSideControllerKind GetController(char side) => NormalizeColor(side, nameof(side)) switch
	{
		'w' => _whiteController,
		_ => _blackController
	};

	/// <summary>
	///     Disposes all underlying engines and cancels outstanding classification.
	/// </summary>
	public async ValueTask DisposeAsync()
    {
        TaskCompletionSource<bool> completion;
        bool ownsCleanup;
        lock (_sync)
        {
            ownsCleanup = _disposeCompletion is null;
            completion = _disposeCompletion ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _disposed, 1);
        }
        if (ownsCleanup)
        {
            try
            {
                await DisposeCoreAsync().ConfigureAwait(false);
                completion.TrySetResult(true);
            }
            catch (Exception error) { completion.TrySetException(error); }
        }
        await completion.Task.ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
		await _commandGate.WaitAsync().ConfigureAwait(false);
		try
		{
			StreamsStopped?.Invoke();
			ClearState();
			if (_engineClients is { } clients)
			{
				clients.Ponder.InfoPvWithGeneration -= OnPonderInfo;
				clients.Ponder.BestMoveWithGeneration -= PonderOnBestMove;
			}
			CoordinatorRuntimeUtilities.CancelAndDispose(ref _bestCts);
			try { await Task.WhenAll(_analysisTasks).ConfigureAwait(false); }
			finally
			{
				_analysisTasks.Clear();
				if (_engineClients is { } engines) await engines.DisposeAsync().ConfigureAwait(false);
			}
		}
		finally { _commandGate.Release(); }
    }

	/// <summary>
	///     Synchronously disposes all underlying engines and cancels outstanding classification.
	/// </summary>
	public void Dispose()
	{
		DisposeAsync().AsTask().GetAwaiter().GetResult();
		GC.SuppressFinalize(this);
	}

	private async Task<T> RunCommandAsync<T>(Func<Task<T>> command, CancellationToken ct)
	{
		await _commandGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(UciGameEngineSession));
			return await command().ConfigureAwait(false);
		}
		finally { _commandGate.Release(); }
	}

	private async Task RunCommandAsync(Func<Task> command, CancellationToken ct)
	{
		await _commandGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(UciGameEngineSession));
			await command().ConfigureAwait(false);
		}
		finally { _commandGate.Release(); }
	}

	/// <summary>
	///     Cancels background move classification and clears its completion promise.
	/// </summary>
	private void ClearState()
	{
		CoordinatorRuntimeUtilities.CancelAndDispose(ref _classificationCts);
		CancelClassificationCompletion();
	}

	private void ResetGameplaySession()
	{
		lock (_sync)
		{
			_gameId                 = Guid.NewGuid();
			_nextMoveId             = 0;
			_appliedMoveHistory     = [];
			_pendingPromotion       = null;
			_forcedResult           = default;
			_declaredDrawMove       = null;
			_claimableResult        = null;
			_drawOfferedBy          = null;
			_clockHistory.Clear();
			_lastResult = default;
		}
	}

	private static char NormalizeColor(char color, string paramName)
	{
		if (color is 'w' or 'b')
			return color;

		throw new ArgumentOutOfRangeException(paramName, "Color must be 'w' or 'b'.");
	}

	private async Task<Move> ClassifyMoveWithClassificationClientAsync(
		Fen               fen,
		string            move,
		CancellationToken ct)
	{
		await RunCommandAsync(async () =>
		{
			if (!IsStarted) throw new InvalidOperationException("Start the primary engine before requesting engine analysis.");
			if (!Engines.Classifier.IsStarted)
				await Engines.Classifier.StartAsync(ct).ConfigureAwait(false);
		}, ct).ConfigureAwait(false);
		var result = await Engines.Classifier.SearchMoveAsync(fen, move, _options.ClassificationDepth, ct)
												.ConfigureAwait(false);
		var boardState = BoardState.FromFen(fen) ??
						 throw new InvalidOperationException(
							 $"Unable to build board state from FEN '{fen.Raw}' for move classification."
						 );
		var score = MoveScore.FromSearchResult(result);
		return new Move(move, MoveAnalysis.Analyze(move, boardState, score, false));
	}

	private ImmutableArray<GameMoveEvent> BuildLoadedMoveHistory(Fen baseFen, IReadOnlyList<string> moves, CancellationToken ct)
	{
		var history = ImmutableArray.CreateBuilder<GameMoveEvent>(moves.Count);
		var position = baseFen;
		var played = ImmutableList<string>.Empty;
		for (int i = 0; i < moves.Count; i++)
		{
			ct.ThrowIfCancellationRequested();
			string notation = moves[i];
			var next = position.ApplyMove(notation);
			var previousState = UciState.Default with { BaseFen = baseFen, CurrentFen = position, PlayedMoves = played };
			played = played.Add(notation);
			var nextState = UciState.Default with { BaseFen = baseFen, CurrentFen = next, PlayedMoves = played, LegalMoves = next.GetLegalMoves().ToImmutableList() };
			if (!CoordinatorMoveFactory.TryBuildFallbackMove(position, notation, out var move))
				throw new InvalidOperationException("Unable to reconstruct the loaded move history.");
			history.Add(CoordinatorMoveFactory.BuildGameMoveEvent(_gameId, i + 1, previousState, nextState, notation, move, GameMoveActor.Remote));
			position = next;
		}
		return history.MoveToImmutable();
	}

	private Task<UciState> LoadPositionSnapshotAsync(
		Fen fen, IReadOnlyCollection<string> moves, CancellationToken ct, Fen? currentFen = null)
	{
		ct.ThrowIfCancellationRequested();
		Fen effectiveFen = currentFen ?? fen;
		if (!currentFen.HasValue)
			foreach (var move in moves)
			{
				ct.ThrowIfCancellationRequested();
				effectiveFen = effectiveFen.ApplyMove(move);
			}
		var legalMoves = effectiveFen.GetLegalMoves();
		InvalidatePonderState(clearTransientState: true);
		CoordinatorRuntimeUtilities.CancelAndDispose(ref _bestCts);
		ClearState();
		QueuePonderStop();
		return Task.FromResult(new UciState(
			fen, effectiveFen, moves as ImmutableList<string> ?? moves.ToImmutableList(), legalMoves.ToImmutableList(),
			ImmutableDictionary<string, Move>.Empty, null, null, null, false));
	}

	// Callers hold _commandGate. Workers never mutate this collection.
	private void PruneCompletedAnalysisTasks() =>
		_analysisTasks.RemoveAll(static task =>
		{
			if (!task.IsCompleted) return false;
			// Worker errors are published through Error. Observe faults left by throwing subscribers as well.
			_ = task.Exception;
			return true;
		});

	private void QueuePonderStop()
	{
		PruneCompletedAnalysisTasks();
		if (_engineClients is not { Ponder.IsStarted: true } engines) return;
		if (_ponderOperation.IsCompletedSuccessfully && !engines.Ponder.HasActiveSearch) return;
		var preceding = _ponderOperation;
		_ponderOperation = Task.Run(async () =>
		{
			try
			{
				await preceding.ConfigureAwait(false);
				await engines.Ponder.StopSearchAsync(CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex) { RaiseError(ex); }
		});
		_analysisTasks.Add(_ponderOperation);
	}

	private Task StartBackgroundWorkAsync(
		Fen baseFen, IReadOnlyCollection<string> moves, Fen currentFen,
		ImmutableList<string> legalMoves, CancellationToken ct)
	{
		PruneCompletedAnalysisTasks();
		int generation = BeginClassificationRun();
		if (legalMoves.Count == 0)
		{
			CompleteClassificationRun(generation);
			return Task.CompletedTask;
		}
		if (!_options.EnableBackgroundAnalysis || !IsStarted || State.Result.IsTerminal)
		{
			_classificationRuntime.CompleteRun(State, generation, out _);
			return Task.CompletedTask;
		}
		var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var token = cts.Token;
		CoordinatorRuntimeUtilities.CancelAndDispose(ref _classificationCts, cts);
		var precedingPonderOperation = _ponderOperation;
		var ponderStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		_ponderOperation = ponderStarted.Task;
		_analysisTasks.Add(Task.Run(async () =>
		{
			try
			{
				await precedingPonderOperation.ConfigureAwait(false);
				token.ThrowIfCancellationRequested();
				await Engines.Ponder.StartSearchAsync(baseFen, moves, token).ConfigureAwait(false);
				lock (_sync)
				{
					if (generation != _classificationRuntime.Generation || token.IsCancellationRequested) return;
					AcceptPonderGeneration(Engines.Ponder.CurrentSearchGeneration);
				}
				SetBackgroundSearchingState(generation);
				ponderStarted.TrySetResult(true);
				foreach (var notation in legalMoves)
				{
					token.ThrowIfCancellationRequested();
					var move = await ClassifyMoveWithClassificationClientAsync(currentFen, notation, token).ConfigureAwait(false);
					ApplyClassifiedMove(move, generation);
				}
				CompleteClassificationRun(generation);
			}
			catch (OperationCanceledException) { CancelClassificationRunIfActive(generation); }
			catch (Exception ex)
			{
				FaultClassificationRun(generation, ex);
				if (!token.IsCancellationRequested) RaiseError(ex);
			}
			finally { ponderStarted.TrySetResult(false); }
		}, CancellationToken.None));
		return Task.CompletedTask;
	}

	private async Task<UciState> ApplyMoveAsync(
		UciState                previousState,
		string                  moveNotation,
		Move                    classifiedMove,
		GameMoveActor           actor,
		PromotionChosenEvent?   promotionChosenEvent,
		CancellationToken       ct)
	{
		ct.ThrowIfCancellationRequested();
		var completedAtUtc = _utcNowProvider();
		EnsureTurnHasTimeRemaining(completedAtUtc);
		var newMoves = previousState.PlayedMoves.Add(moveNotation);
		var snapshot = await LoadPositionSnapshotAsync(previousState.BaseFen, newMoves, ct, previousState.CurrentFen.ApplyMove(moveNotation)).ConfigureAwait(false);
		AdvanceClockForCompletedMove(previousState.CurrentFen.ActiveColor, completedAtUtc);
		_forcedResult = default;
		_declaredDrawMove = null;
		if (_drawOfferPolicy == PlayableMatchDrawOfferPolicy.ExpireOnMove &&
			_drawOfferedBy.HasValue && _drawOfferedBy != previousState.CurrentFen.ActiveColor)
			_drawOfferedBy = null;
		long moveId = Interlocked.Increment(ref _nextMoveId);

		var moveEvent = CoordinatorMoveFactory.BuildGameMoveEvent(
			_gameId,
			moveId,
			previousState,
			snapshot,
			moveNotation,
			classifiedMove,
			actor
		);

		lock (_sync)
		{
			_appliedMoveHistory = _appliedMoveHistory.Add(moveEvent);
			_pendingPromotion   = null;
		}

		var metadata = ApplyMatchMetadata(snapshot);
		snapshot = metadata.Snapshot;

		if (promotionChosenEvent.HasValue)
			RaiseTypedEvent(
				PromotionChosen,
				promotionChosenEvent.Value,
				UciGameEngineSessionEvent.ForPromotionChosen(promotionChosenEvent.Value)
			);

		RaiseTypedEvent(MoveMade, moveEvent, UciGameEngineSessionEvent.ForMove(UciGameEngineSessionEventKind.MoveMade, moveEvent));
		if (moveEvent.KindFlags.HasFlag(GameMoveKindFlags.Capture))
			RaiseTypedEvent(CaptureMade, moveEvent, UciGameEngineSessionEvent.ForMove(UciGameEngineSessionEventKind.CaptureMade, moveEvent));
		if (moveEvent.KindFlags.HasFlag(GameMoveKindFlags.KingsideCastling) ||
			moveEvent.KindFlags.HasFlag(GameMoveKindFlags.QueensideCastling))
			RaiseTypedEvent(CastlingMade, moveEvent, UciGameEngineSessionEvent.ForMove(UciGameEngineSessionEventKind.CastlingMade, moveEvent));
		if (moveEvent.KindFlags.HasFlag(GameMoveKindFlags.EnPassant))
			RaiseTypedEvent(EnPassantMade, moveEvent, UciGameEngineSessionEvent.ForMove(UciGameEngineSessionEventKind.EnPassantMade, moveEvent));
		if (moveEvent.IsCheck)
			RaiseTypedEvent(Check, moveEvent, UciGameEngineSessionEvent.ForMove(UciGameEngineSessionEventKind.Check, moveEvent));
		if (moveEvent.IsCheckmate)
			RaiseTypedEvent(Checkmate, moveEvent, UciGameEngineSessionEvent.ForMove(UciGameEngineSessionEventKind.Checkmate, moveEvent));
		if (moveEvent.IsStalemate)
			RaiseTypedEvent(Stalemated, moveEvent, UciGameEngineSessionEvent.ForMove(UciGameEngineSessionEventKind.Stalemated, moveEvent));
		if (metadata.ResultChanged)
			RaiseStateEvent(ResultChanged, UciGameEngineSessionEventKind.ResultChanged, snapshot);

		PublishGameOver(snapshot);
		if (previousState.CurrentFen.ActiveColor != snapshot.CurrentFen.ActiveColor)
		{
			var turnChanged = new TurnChangedEvent(
				_gameId,
				previousState.CurrentFen.ActiveColor,
				snapshot.CurrentFen.ActiveColor,
				snapshot.CurrentFen,
				DateTimeOffset.UtcNow
			);
			RaiseTypedEvent(TurnChanged, turnChanged, UciGameEngineSessionEvent.ForTurnChanged(turnChanged));
		}
		PublishPositionChanged(snapshot);
		RaiseStateEvent(LegalMovesUpdated, UciGameEngineSessionEventKind.LegalMovesUpdated, snapshot);

		await StartBackgroundWorkAsync(
			previousState.BaseFen,
			newMoves,
			snapshot.CurrentFen,
			snapshot.LegalMoves,
			ct
		).ConfigureAwait(false);
		return State;
	}

	private void RejectIllegalMove(
		string                 move,
		string                 reason,
		IReadOnlyList<string>  legalMoves,
		bool                   isPromotionChoicePending)
	{
		var illegalMove = new IllegalMoveRejectedEvent(
			_gameId,
			move,
			reason,
			[.. legalMoves],
			isPromotionChoicePending,
			DateTimeOffset.UtcNow
		);
		RaiseTypedEvent(IllegalMoveRejected, illegalMove, UciGameEngineSessionEvent.ForIllegalMove(illegalMove));
		throw new ArgumentException(reason, nameof(move));
	}

	private UciState UpdateMatchMetadata(char? drawOfferedBy = null, bool updateDrawOffer = false, DateTimeOffset? snapshotUtc = null)
	{
		if (updateDrawOffer)
			_drawOfferedBy = drawOfferedBy;

		MetadataUpdate update;
		lock (_sync) update = ApplyMatchMetadata(_state, snapshotUtc);
		RaiseStateEvent(StateChanged, UciGameEngineSessionEventKind.StateChanged, update.Snapshot);
		if (update.ResultChanged)
		{
			RaiseStateEvent(ResultChanged, UciGameEngineSessionEventKind.ResultChanged, update.Snapshot);
			PublishGameOver(update.Snapshot);
		}

		return update.Snapshot;
	}

	private UciState SetForcedResult(PlayableMatchResult result, string? declaredDrawMove = null, DateTimeOffset? completedAtUtc = null)
	{
		if (State.Result.IsTerminal)
			throw new InvalidOperationException("The match has already ended.");
		_forcedResult = result;
		_declaredDrawMove = declaredDrawMove;
		_claimableResult = null;
		_drawOfferedBy = null;
		return UpdateMatchMetadata(snapshotUtc: completedAtUtc);
	}

	private MetadataUpdate ApplyMatchMetadata(UciState state, DateTimeOffset? snapshotUtc = null)
	{
		var clock = GetClockSnapshot(snapshotUtc);
		var outcome = EvaluateOutcome(state.BaseFen, state.CurrentFen, state.LegalMoves, clock);
		if (outcome.Result.IsTerminal)
		{
			if (!_lastResult.IsTerminal)
			{
				Interlocked.Exchange(ref _acceptedPonderGeneration, 0);
				CoordinatorRuntimeUtilities.CancelAndDispose(ref _bestCts);
				ClearState();
				QueuePonderStop();
				state = state with { IsSearching = false, BestMove = null, PonderMove = null, Evaluation = null };
			}
			_forcedResult = outcome.Result;
			_pendingPromotion = null;
			_drawOfferedBy = null;
			if (_timeControl.HasValue && _clockHistory.Count > 0 && !_clockHistory[^1].PausedAtUtc.HasValue)
			{
				_clockHistory[^1] = PlayableMatchClockKernel.Pause(_clockHistory[^1], clock!.Value.SnapshotUtc);
				clock = GetClockSnapshot(clock.Value.SnapshotUtc);
			}
		}
		var snapshot = state with
		{
			Result = outcome.Result,
			ClaimableResult = outcome.ClaimableResult,
			DrawOfferedBy = _drawOfferedBy,
			Clock = clock
		};

		bool resultChanged;
		lock (_sync)
		{
			resultChanged = _lastResult != outcome.Result;
			_claimableResult = outcome.ClaimableResult;
			_lastResult = outcome.Result;
			_state = snapshot;
			Interlocked.Increment(ref _stateVersion);
		}

		return new(snapshot, resultChanged);
	}

	private static bool TryResolveFallbackControlledMove(UciState state, out string move)
	{
		move = string.Empty;
		if (state.LegalMoves.Count == 0)
			return false;

		string? checkMove = null;
		string? captureMove = null;
		string? promotionMove = null;

		foreach (var legalMove in state.LegalMoves)
		{
			var classification = state.CurrentFen.ClassifyMoveFully(legalMove);
			if (classification.IsMate)
			{
				move = legalMove;
				return true;
			}

			if (checkMove is null && classification.IsCheck)
				checkMove = legalMove;

			if (captureMove is null && classification.IsCapture)
				captureMove = legalMove;

			if (promotionMove is null && classification.IsPromotion)
				promotionMove = legalMove;
		}

		move = checkMove ?? captureMove ?? promotionMove ?? state.LegalMoves[0];
		return true;
	}

	private PlayableMatchClockRestore? CreateClockRestore(Fen currentFen, PlayableMatchClockSetup? setup)
	{
		if (!setup.HasValue)
			return null;

		var clockSetup = setup.Value;
		var now = clockSetup.SnapshotUtc ?? _utcNowProvider();
		return PlayableMatchClockKernel.CreateRestore(currentFen, clockSetup, _timeControl, now);
	}

	private void InitializeClocks(char activeColor)
	{
		_clockHistory.Clear();
		_clockHistoryOriginMoveCount = 0;
		if (!_timeControl.HasValue)
			return;

		_clockHistory.Add(PlayableMatchClockKernel.Initialize(_timeControl.Value, activeColor, _utcNowProvider()));
	}

	private void RestoreClock(char activeColor, PlayableMatchClockRestore restore)
	{
		if (!_timeControl.HasValue)
			throw new InvalidOperationException("Cannot restore clock state because this session has no time control.");

		_clockHistory.Clear();
		_clockHistory.Add(PlayableMatchClockKernel.Restore(_timeControl.Value, activeColor, restore));
	}

	private void AdvanceClockForCompletedMove(char movingSide, DateTimeOffset? completedAtUtc = null)
	{
		if (!_timeControl.HasValue || _clockHistory.Count == 0)
			return;

		var now = completedAtUtc ?? _utcNowProvider();
		_clockHistory.Add(PlayableMatchClockKernel.CompleteMove(_timeControl.Value, _clockHistory[^1], movingSide, now));
	}

	private PlayableMatchClockState? GetClockSnapshot(DateTimeOffset? snapshotUtc = null)
	{
		if (!_timeControl.HasValue || _clockHistory.Count == 0)
			return null;

		var now = snapshotUtc ?? _utcNowProvider();
		return PlayableMatchClockKernel.Snapshot(_timeControl.Value, _clockHistory[^1], now);
	}

	private void EnsureTurnHasTimeRemaining(DateTimeOffset? snapshotUtc = null)
	{
		if (_timeControl.HasValue && _timeControl.Value.TimeoutPolicy == PlayableMatchTimeoutPolicy.Ignore)
			return;

		var clock = GetClockSnapshot(snapshotUtc);
		if (!clock.HasValue)
			return;

		var currentRemaining = clock.Value.ActiveColor == 'w'
			? clock.Value.WhiteRemaining
			: clock.Value.BlackRemaining;
		if (currentRemaining > TimeSpan.Zero)
			return;

		UpdateMatchMetadata();
		throw new InvalidOperationException("The current side has already lost on time.");
	}

	private MatchOutcome EvaluateOutcome(
		Fen baseFen, Fen fen, IReadOnlyList<string> legalMoves, PlayableMatchClockState? clock)
	{
		if (_forcedResult.IsTerminal)
			return new(_forcedResult, null);
		var outcome = PlayableMatchAdjudication.Evaluate(fen, legalMoves.Count, clock, _timeControl,
			CountRepetitions(baseFen, fen), _claimableDrawPolicy);
		return new(outcome.Result, outcome.ClaimableResult);
	}

	private int CountRepetitions(Fen baseFen, Fen currentFen)
	{
		string currentKey = LocalPositionRules.BuildRepetitionKey(currentFen);
		var count = LocalPositionRules.BuildRepetitionKey(baseFen) == currentKey ? 1 : 0;
		foreach (var move in _appliedMoveHistory)
		{
			if (LocalPositionRules.BuildRepetitionKey(move.ResultingFen) == currentKey)
				count++;
		}

		return count;
	}


	private static char Opposite(char color) => color == 'w' ? 'b' : 'w';

	private async Task RollbackStartAsync(bool snapshotStarted, bool ponderStarted, bool classifierStarted)
	{
		if (classifierStarted)
			try
			{
				await Engines.Classifier.StopAsync(CancellationToken.None).ConfigureAwait(false);
			}
			catch
			{
				/* best-effort */
			}

		if (ponderStarted)
			try
			{
				await Engines.Ponder.StopAsync(CancellationToken.None).ConfigureAwait(false);
			}
			catch
			{
				/* best-effort */
			}

		if (snapshotStarted)
			try
			{
				await Engines.Snapshot.StopAsync(CancellationToken.None).ConfigureAwait(false);
			}
			catch
			{
				/* best-effort */
			}

		ClearState();
		Interlocked.Exchange(ref _acceptedPonderGeneration, 0);
		InvalidatePonderState(clearTransientState: true);
	}

	private void SetSearchingState(bool isSearching)
	{
		UciState snapshot;
		lock (_sync)
		{
			if (_state.IsSearching == isSearching) return;

			_state    = _state with { IsSearching = isSearching };
			Interlocked.Increment(ref _stateVersion);
			snapshot = _state;
		}

		PublishSearchStateChanged(snapshot);
		if (isSearching)
			RaiseStateEvent(EngineThinkingStarted, UciGameEngineSessionEventKind.EngineThinkingStarted, snapshot);
		else
			RaiseStateEvent(EngineThinkingStopped, UciGameEngineSessionEventKind.EngineThinkingStopped, snapshot);
	}

	private void SetBackgroundSearchingState(int generation)
	{
		var generationGuard = new AnalysisGenerationGuard(this, generation, isPonder: false);
		UciState snapshot;
		long version;
		lock (_sync)
		{
			if (!generationGuard.IsCurrent || _state.IsSearching) return;
			_state = _state with { IsSearching = true };
			version = Interlocked.Increment(ref _stateVersion);
			snapshot = _state;
		}
		var guard = new AnalysisGenerationGuard(this, generation, isPonder: false, version);
		RaiseStateEventForAnalysis(StateChanged, UciGameEngineSessionEventKind.StateChanged, snapshot, guard);
		RaiseStateEventForAnalysis(SearchStateChanged, UciGameEngineSessionEventKind.SearchStateChanged, snapshot, guard);
		RaiseStateEventForAnalysis(EngineThinkingStarted, UciGameEngineSessionEventKind.EngineThinkingStarted, snapshot, guard);
	}

	private void AcceptPonderGeneration(int generation) =>
		Interlocked.Exchange(ref _acceptedPonderGeneration, generation);

	internal int AcceptedPonderGenerationForTests => Volatile.Read(ref _acceptedPonderGeneration);
	internal int AcceptedClassificationGenerationForTests
	{
		get
		{
			return _classificationRuntime.Generation;
		}
	}

	internal void ApplyPonderBestMoveForTests(ParsedMove best, ParsedMove? ponder, int generation) =>
		ApplyPonderBestMove(best, ponder, generation);

	internal void ApplyPonderInfoForTests(PrincipalVariation pv, int generation) =>
		ApplyPonderInfo(pv, generation);

	internal void ApplyClassifiedMoveForTests(Move move, int generation) =>
		ApplyClassifiedMove(move, generation);

	internal void CompleteClassificationForTests(int generation) =>
		CompleteClassificationRun(generation);

	private void InvalidatePonderState(bool clearTransientState)
	{
		Interlocked.Exchange(ref _acceptedPonderGeneration, 0);

		UciState snapshot;
		bool     changed = false;
		lock (_sync)
		{
			var next = _state with { IsSearching = false };
			if (clearTransientState)
				next = next with
				{
					Evaluation = null,
					BestMove   = null,
					PonderMove = null
				};

			if (next == _state) return;

			_state   = next;
			Interlocked.Increment(ref _stateVersion);
			snapshot = _state;
			changed  = true;
		}

		if (changed)
			PublishSearchStateChanged(snapshot);
	}

	private int BeginClassificationRun() => _classificationRuntime.BeginRun();

	private void ApplyClassifiedMove(Move move, int generation)
	{
		if (Volatile.Read(ref _disposed) != 0) return;
		UciState? snapshot;
		long version;
		lock (_sync)
		{
			_classificationRuntime.ApplyClassifiedMove(ref _state, move, generation, out snapshot);
			version = snapshot.HasValue ? Interlocked.Increment(ref _stateVersion) : 0;
		}
		if (!snapshot.HasValue)
			return;

		var guard = new AnalysisGenerationGuard(this, generation, isPonder: false);
		RaiseStateEventForAnalysis(StateChanged, UciGameEngineSessionEventKind.StateChanged, snapshot.Value,
			new AnalysisGenerationGuard(this, generation, isPonder: false, version));
		RaiseTypedEventForAnalysis(MoveClassified, move, UciGameEngineSessionEvent.ForClassifiedMove(move), guard);
#pragma warning disable CS0618
		_events.RaiseIfCurrent(MoveClassificationUpdated, move, guard);
#pragma warning restore CS0618
	}

	private void CompleteClassificationRun(int generation)
	{
		UciState? snapshot;
		lock (_sync)
			_classificationRuntime.CompleteRun(_state, generation, out snapshot);
		var completed = ClassificationCompleted;
		var published = EventPublished;
		if (snapshot.HasValue && (completed is not null || published is not null))
			DispatchClassificationCompletion(generation, completed, published);
	}

	private void DispatchClassificationCompletion(int generation, Action<UciState>? completed,
		Action<UciGameEngineSessionEvent>? published)
	{
		// Completion survives unrelated state updates. Each UI delivery reads a fresh snapshot while
		// atomically checking that it still belongs to the completed classification generation.
		if (completed is not null)
			_events.Raise(() =>
			{
				if (TryGetClassificationCompletionState(generation, out var state)) completed(state);
			});
		if (published is not null)
			_events.Raise(() =>
			{
				if (TryGetClassificationCompletionState(generation, out var state))
					published(UciGameEngineSessionEvent.ForState(UciGameEngineSessionEventKind.ClassificationCompleted, state));
			});
	}

	private bool TryGetClassificationCompletionState(int generation, out UciState state)
	{
		lock (_sync)
		{
			state = _state;
			return Volatile.Read(ref _disposed) == 0 && generation == _classificationRuntime.Generation;
		}
	}

	private void FaultClassificationRun(int generation, Exception ex) =>
		_classificationRuntime.FaultRun(generation, ex);

	private void CancelClassificationRunIfActive(int generation) =>
		_classificationRuntime.CancelRunIfActive(generation);

	private void CancelClassificationCompletion() =>
		_classificationRuntime.CancelCompletion();

	/// <summary>
	///     Forwards ponder engine PV updates.
	/// </summary>
	private void OnPonderInfo(PrincipalVariation pv, int generation) =>
		ApplyPonderInfo(pv, generation);

	private void ApplyPonderInfo(PrincipalVariation pv, int generation)
	{
		if (generation != Volatile.Read(ref _acceptedPonderGeneration)) return;

		UciState snapshot;
		long version;
		lock (_sync)
		{
			if (generation == 0 || generation != Volatile.Read(ref _acceptedPonderGeneration)) return;
			_state   = _state with { Evaluation = pv };
			version = Interlocked.Increment(ref _stateVersion);
			snapshot = _state;
		}

		var guard = new AnalysisGenerationGuard(this, generation, isPonder: true);
		RaiseStateEventForAnalysis(StateChanged, UciGameEngineSessionEventKind.StateChanged, snapshot,
			new AnalysisGenerationGuard(this, generation, isPonder: true, version));
		RaiseTypedEventForAnalysis(EvaluationChanged, pv, UciGameEngineSessionEvent.ForEvaluation(pv), guard);
#pragma warning disable CS0618
		_events.RaiseIfCurrent(EvaluationUpdated, pv, guard);
#pragma warning restore CS0618
	}

	/// <summary>
	///     Forwards the best move (and optional ponder move) found by the ponder engine.
	/// </summary>
	private void PonderOnBestMove(string best, string? ponder, int generation) =>
		ApplyPonderBestMove(
			ParsedMove.FromNotation(best),
			string.IsNullOrWhiteSpace(ponder) ? null : ParsedMove.FromNotation(ponder),
			generation
		);

	private void ApplyPonderBestMove(ParsedMove best, ParsedMove? ponder, int generation)
	{
		if (generation != Volatile.Read(ref _acceptedPonderGeneration)) return;

		UciState snapshot;
		long version;
		lock (_sync)
		{
			if (generation == 0 || generation != Volatile.Read(ref _acceptedPonderGeneration)) return;
			_state   = _state with { BestMove = best, PonderMove = ponder };
			version = Interlocked.Increment(ref _stateVersion);
			snapshot = _state;
		}

		var guard = new AnalysisGenerationGuard(this, generation, isPonder: true, version);
		RaiseStateEventForAnalysis(StateChanged, UciGameEngineSessionEventKind.StateChanged, snapshot, guard);
		RaiseStateEventForAnalysis(BestMoveChanged, UciGameEngineSessionEventKind.BestMoveChanged, snapshot, guard);
	}

	private readonly struct AnalysisGenerationGuard(UciGameEngineSession owner, int generation, bool isPonder, long stateVersion = 0)
		: IEventDeliveryGuard
	{
		public bool IsCurrent => Volatile.Read(ref owner._disposed) == 0 &&
			(isPonder ? generation != 0 && generation == Volatile.Read(ref owner._acceptedPonderGeneration)
				: generation == owner._classificationRuntime.Generation) &&
			(stateVersion == 0 || stateVersion == Volatile.Read(ref owner._stateVersion));
	}

	private readonly struct StateVersionGuard(UciGameEngineSession owner, long version) : IEventDeliveryGuard
	{
		public bool IsCurrent => Volatile.Read(ref owner._disposed) == 0 && version == Volatile.Read(ref owner._stateVersion);
	}

	private void RaiseStateEventForAnalysis(Action<UciState>? handler, UciGameEngineSessionEventKind kind,
		UciState state, AnalysisGenerationGuard guard)
	{
		// Internal stream subscribers only write to channels. Keep validation and that write atomic with
		// generation invalidation; application callbacks are dispatched afterwards, outside this lock.
		lock (_sync)
		{
			if (!guard.IsCurrent) return;
			if (kind == UciGameEngineSessionEventKind.StateChanged) StateObserved?.Invoke(state);
		}
		_events.RaiseIfCurrent(handler, state, guard);
		_events.RaiseIfCurrent(EventPublished, UciGameEngineSessionEvent.ForState(kind, state), guard);
	}

	private void RaiseTypedEventForAnalysis<T>(Action<T>? handler, T payload,
		UciGameEngineSessionEvent sessionEvent, AnalysisGenerationGuard guard)
	{
		_events.RaiseIfCurrent(handler, payload, guard);
		_events.RaiseIfCurrent(EventPublished, sessionEvent, guard);
	}

	private void RaiseSessionEvent(Action? legacyHandler, UciGameEngineSessionEventKind kind)
	{
		_events.Raise(legacyHandler);
		_events.Raise(EventPublished, UciGameEngineSessionEvent.Create(kind));
	}

	private void RaiseStateEvent(
		Action<UciState>?              legacyHandler,
		UciGameEngineSessionEventKind  kind,
		UciState                       state)
	{
		if (kind == UciGameEngineSessionEventKind.StateChanged)
		{
			if (legacyHandler is null && EventPublished is null && StateObserved is null) return;
			StateVersionGuard guard;
			lock (_sync)
			{
				if (state != _state) return;
				guard = new StateVersionGuard(this, _stateVersion);
				StateObserved?.Invoke(state);
			}
			_events.RaiseIfCurrent(legacyHandler, state, guard);
			_events.RaiseIfCurrent(EventPublished, UciGameEngineSessionEvent.ForState(kind, state), guard);
			return;
		}
		_events.Raise(legacyHandler, state);
		_events.Raise(EventPublished, UciGameEngineSessionEvent.ForState(kind, state));
	}

	private void RaiseTypedEvent<T>(
		Action<T>?                     legacyHandler,
		T                              payload,
		UciGameEngineSessionEvent      sessionEvent)
	{
		_events.Raise(legacyHandler, payload);
		_events.Raise(EventPublished, sessionEvent);
	}

	private void PublishPositionChanged(UciState snapshot)
	{
		RaiseStateEvent(StateChanged, UciGameEngineSessionEventKind.StateChanged, snapshot);
		RaiseStateEvent(PositionChanged, UciGameEngineSessionEventKind.PositionChanged, snapshot);
	}

	private void PublishGameOver(UciState snapshot)
	{
		if (snapshot.IsGameOver)
		{
			GameFinishedEvent finished;
			lock (_sync) finished = new(_gameId, snapshot);
			RaiseStateEvent(GameOver, UciGameEngineSessionEventKind.GameOver, snapshot);
			RaiseTypedEvent(GameFinished, finished, UciGameEngineSessionEvent.ForGameFinished(finished));
		}
	}

	private void PublishSearchStateChanged(UciState snapshot)
	{
		RaiseStateEvent(StateChanged, UciGameEngineSessionEventKind.StateChanged, snapshot);
		RaiseStateEvent(SearchStateChanged, UciGameEngineSessionEventKind.SearchStateChanged, snapshot);
	}

	/// <summary>
	///     Raises the Error event with the specified exception.
	/// </summary>
	private void RaiseError(Exception ex)
	{
		RaiseTypedEvent(Error, ex, UciGameEngineSessionEvent.ForError(ex));
#pragma warning disable CS0618
		_events.Raise(EngineError, ex);
#pragma warning restore CS0618
	}

	private async Task StopSearchCoreAsync(CancellationToken ct)
	{
		await _ponderOperation.ConfigureAwait(false);
		InvalidatePonderState(clearTransientState: true);
		CoordinatorRuntimeUtilities.CancelAndDispose(ref _bestCts);

		try
		{
			if (_engineClients is { Ponder.IsStarted: true } engines)
				await engines.Ponder.StopSearchAsync(ct).ConfigureAwait(false);
		}
		finally
		{
			SetSearchingState(false);
		}
	}

	private async Task<bool> TryStopStepAsync(
		Func<CancellationToken, Task> step,
		bool                          cancellationRequested,
		List<Exception>               failures,
		CancellationToken             callerToken,
		bool                          reportErrors = true)
	{
		try
		{
			await step(cancellationRequested ? CancellationToken.None : callerToken).ConfigureAwait(false);
			return cancellationRequested;
		}
		catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
		{
			return true;
		}
		catch (OperationCanceledException)
		{
			return cancellationRequested;
		}
		catch (Exception ex)
		{
			failures.Add(ex);
			if (reportErrors)
				RaiseError(ex);

			return cancellationRequested;
		}
	}

	private readonly record struct MatchOutcome(
		PlayableMatchResult  Result,
		PlayableMatchResult? ClaimableResult
	);

	private readonly record struct SavedMatchRestore(PlayableMatchSave Save, ValidatedPlayableMatchSave Validated);

	private readonly record struct MetadataUpdate(
		UciState Snapshot,
		bool     ResultChanged
	);
}

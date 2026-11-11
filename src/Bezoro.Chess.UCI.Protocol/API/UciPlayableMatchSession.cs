using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.ExceptionServices;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.Chess.UCI.Protocol.Internal;

namespace Bezoro.Chess.UCI.Protocol.API;

/// <summary>
///     Coordinates a playable match using optional separate playing and full-strength analysis clients, plus local
///     board-state ownership for legal moves, promotion flow, clocks, and result adjudication.
/// </summary>
/// <remarks>
///     Local commands and snapshot reads serialize state access. Engine searches run outside the state gate and reject
///     stale results after a position change. Events run after each committed operation, outside the gate, on its caller;
///     concurrent callers may deliver callbacks concurrently. Retain event payloads or immutable snapshots for UI dispatch.
/// </remarks>
public sealed class UciPlayableMatchSession : IDisposable, IAsyncDisposable
{
	private readonly object _stateGate = new();
    private int _operationDepth;
    private long _clockObservationVersion;
    private DateTimeOffset? _operationUtc;
    private List<PendingEvent>? _pendingEvents;
    private List<PendingEvent>? _spareEvents;
    private List<Action>? _pendingCancellations;
    private List<Action>? _spareCancellations;
    private TaskCompletionSource<bool>? _shutdownCompleted;
    private ExceptionDispatchInfo? _shutdownFailure;
    private AsyncLocal<BatchCapture?>? _batchEvents;
    private Action<PlayableMatchEvent>? _eventHandlers;
    private Delegate[] _eventSnapshot = [];
    private readonly SemaphoreSlim? _engineTurnGate;
    private readonly CancellationTokenSource? _engineLifetimeCancellation;
    private int _activeEngineTurns;
    private bool _engineShutdownSignaled;
    private bool _engineResourcesDisposed;
    private TaskCompletionSource<bool>? _engineTurnsStopped;
    private readonly MoveClassificationCoordinator  _classifications = new();
    private readonly List<PlayableMatchClockCheckpoint> _clockHistory = [];
	private readonly int                            _engineMoveTimeMs;
	private readonly List<PlayedMove>               _moveHistory = [];
	private ImmutableArray<PlayedMove> _moveHistorySnapshot = [];
	private bool _moveHistorySnapshotDirty;
	private long _enrichedClassificationVersion;
	private readonly List<string>                   _playedMoves = [];
	private readonly Dictionary<string, int>         _repetitionCounts = new(StringComparer.Ordinal);
	private readonly List<string>                    _repetitionKeys = [];
	private Fen? _cachedLegalMovesFen;
	private ImmutableArray<string> _cachedLegalMoves;
	private Func<string, PositionAnalysisResult?>? _positionAnalysisResolver;
	private readonly UciEngineClient?               _playingClient;
	private readonly UciPositionAnalysisCoordinator? _positionAnalysis;
	private readonly PlayableMatchClaimableDrawPolicy _claimableDrawPolicy;
	private readonly PlayableMatchControlledMoveFallbackPolicy _controlledMoveFallbackPolicy;
	private readonly PlayableMatchDrawOfferPolicy _drawOfferPolicy;
	private readonly PlayableMatchTimeControl?      _timeControl;
	private readonly Func<DateTimeOffset>           _utcNowProvider;
	private          bool                           _hasCurrentState;
	private          long                           _positionRevision;
	private          int                            _clockHistoryOriginMoveCount;
	private          Fen                            _baseFen = Fen.Default;
	private          PlayableMatchState             _currentState;
	private          PlayableMatchResult?           _claimableResult;
	private          char?                          _drawOfferedBy;
	private          PlayableMatchResult            _forcedResult;
	private          string?                        _declaredDrawMove;
	private          PlayableMatchResult            _lastResult;
	private          PendingPromotionRequest?       _pendingPromotion;
	private          bool                           _disposed;

	/// <summary>
	///     Occurs whenever the session emits a canonical protocol-side event.
	/// </summary>
	public event Action<PlayableMatchEvent>? EventOccurred
    {
        add
        {
            if (value is null) return;
            lock (_stateGate)
            {
                _eventHandlers += value;
                _eventSnapshot = _eventHandlers!.GetInvocationList();
            }
        }
        remove
        {
            if (value is null) return;
            lock (_stateGate)
            {
                _eventHandlers -= value;
                _eventSnapshot = _eventHandlers?.GetInvocationList() ?? [];
            }
        }
    }

	/// <summary>
	///     Creates a local manual-versus-manual match. No engine, process, transport, or background engine analysis is used.
	/// </summary>
	public UciPlayableMatchSession(
		char perspectiveColor = 'w',
		PlayableMatchClaimableDrawPolicy claimableDrawPolicy = PlayableMatchClaimableDrawPolicy.Automatic,
		PlayableMatchDrawOfferPolicy drawOfferPolicy = PlayableMatchDrawOfferPolicy.ExpireOnMove,
		PlayableMatchTimeControl? timeControl = null,
		Func<DateTimeOffset>? utcNowProvider = null)
	{
		PerspectiveColor = NormalizeColor(perspectiveColor, nameof(perspectiveColor));
		if (claimableDrawPolicy is not (PlayableMatchClaimableDrawPolicy.Automatic or PlayableMatchClaimableDrawPolicy.ClaimRequired))
			throw new ArgumentOutOfRangeException(nameof(claimableDrawPolicy));
		if (drawOfferPolicy is not (PlayableMatchDrawOfferPolicy.ExpireOnMove or PlayableMatchDrawOfferPolicy.PersistUntilResponse))
			throw new ArgumentOutOfRangeException(nameof(drawOfferPolicy));
		WhiteController = MatchSideControllerKind.Manual;
		BlackController = MatchSideControllerKind.Manual;
		_claimableDrawPolicy = claimableDrawPolicy;
		_drawOfferPolicy = drawOfferPolicy;
		_controlledMoveFallbackPolicy = PlayableMatchControlledMoveFallbackPolicy.Throw;
		_engineMoveTimeMs = 1_000;
		_timeControl = timeControl;
		_timeControl?.Validate();
		_utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
	}

	/// <summary>Gets whether this session has explicitly configured engine clients for analysis and controlled turns.</summary>
	public bool HasEngineClients => _playingClient is not null;

	/// <summary>Gets the initial position from which the complete played-move history begins.</summary>
	public Fen BaseFen { get { using var operation = EnterOperation(); return _baseFen; } }

	/// <summary>
	///     Creates a playable match session.
	/// </summary>
	public UciPlayableMatchSession(
		UciEngineClient            playingClient,
		UciEngineClient            analysisClient,
		char                       perspectiveColor,
		MatchSideControllerKind    whiteController,
		MatchSideControllerKind    blackController,
		int                        engineMoveTimeMs       = 1_000,
		int                        analysisMoveTimeMs     = 3_000,
		int                        analysisFallbackMoveTimeMs = 250,
		PlayableMatchClaimableDrawPolicy claimableDrawPolicy = PlayableMatchClaimableDrawPolicy.Automatic,
		PlayableMatchDrawOfferPolicy drawOfferPolicy = PlayableMatchDrawOfferPolicy.ExpireOnMove,
		PlayableMatchControlledMoveFallbackPolicy controlledMoveFallbackPolicy = PlayableMatchControlledMoveFallbackPolicy.UseLocalFallback,
		PlayableMatchTimeControl?  timeControl            = null,
		Func<DateTimeOffset>?      utcNowProvider         = null)
	{
		_playingClient    = playingClient ?? throw new ArgumentNullException(nameof(playingClient));
		if (analysisClient is null) throw new ArgumentNullException(nameof(analysisClient));
		if (ReferenceEquals(playingClient, analysisClient))
			throw new ArgumentException("Playing and analysis require distinct dedicated clients.", nameof(analysisClient));
		_engineMoveTimeMs = ValidatePositive(engineMoveTimeMs, nameof(engineMoveTimeMs));
		PerspectiveColor  = NormalizeColor(perspectiveColor, nameof(perspectiveColor));
		if (whiteController is not (MatchSideControllerKind.Manual or MatchSideControllerKind.Engine))
			throw new ArgumentOutOfRangeException(nameof(whiteController));
		if (blackController is not (MatchSideControllerKind.Manual or MatchSideControllerKind.Engine))
			throw new ArgumentOutOfRangeException(nameof(blackController));
		if (claimableDrawPolicy is not (PlayableMatchClaimableDrawPolicy.Automatic or PlayableMatchClaimableDrawPolicy.ClaimRequired))
			throw new ArgumentOutOfRangeException(nameof(claimableDrawPolicy));
		if (drawOfferPolicy is not (PlayableMatchDrawOfferPolicy.ExpireOnMove or PlayableMatchDrawOfferPolicy.PersistUntilResponse))
			throw new ArgumentOutOfRangeException(nameof(drawOfferPolicy));
		if (controlledMoveFallbackPolicy is not (PlayableMatchControlledMoveFallbackPolicy.UseLocalFallback or PlayableMatchControlledMoveFallbackPolicy.Throw))
			throw new ArgumentOutOfRangeException(nameof(controlledMoveFallbackPolicy));
		WhiteController   = whiteController;
		BlackController   = blackController;
		_utcNowProvider   = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
		_claimableDrawPolicy = claimableDrawPolicy;
		_drawOfferPolicy = drawOfferPolicy;
		_controlledMoveFallbackPolicy = controlledMoveFallbackPolicy;
		_timeControl      = timeControl;
		_timeControl?.Validate();
		_positionAnalysis = new(
			analysisClient,
			ValidatePositive(analysisMoveTimeMs, nameof(analysisMoveTimeMs)),
			ValidatePositive(analysisFallbackMoveTimeMs, nameof(analysisFallbackMoveTimeMs))
		);
        _engineTurnGate = new(1, 1);
        _engineLifetimeCancellation = new();
	}

	/// <summary>
	///     Creates a human-versus-engine playable match session using the supplied player color as the analysis
	///     perspective.
	/// </summary>
	public UciPlayableMatchSession(
		UciEngineClient           playingClient,
		UciEngineClient           analysisClient,
		char                      playerColor,
		int                       engineMoveTimeMs       = 1_000,
		int                       analysisMoveTimeMs = 3_000,
		int                       analysisFallbackMoveTimeMs = 250,
		PlayableMatchClaimableDrawPolicy claimableDrawPolicy = PlayableMatchClaimableDrawPolicy.Automatic,
		PlayableMatchDrawOfferPolicy drawOfferPolicy = PlayableMatchDrawOfferPolicy.ExpireOnMove,
		PlayableMatchControlledMoveFallbackPolicy controlledMoveFallbackPolicy = PlayableMatchControlledMoveFallbackPolicy.UseLocalFallback,
		PlayableMatchTimeControl? timeControl            = null,
		Func<DateTimeOffset>?     utcNowProvider         = null)
		: this(
			playingClient,
			analysisClient,
			perspectiveColor: playerColor,
			whiteController: playerColor == 'w' ? MatchSideControllerKind.Manual : MatchSideControllerKind.Engine,
			blackController: playerColor == 'b' ? MatchSideControllerKind.Manual : MatchSideControllerKind.Engine,
			engineMoveTimeMs,
			analysisMoveTimeMs,
			analysisFallbackMoveTimeMs,
			claimableDrawPolicy,
			drawOfferPolicy,
			controlledMoveFallbackPolicy,
			timeControl,
			utcNowProvider
        )
    { }

	/// <summary>
	///     Gets the side used for player-relative advantage and board-orientation helpers.
	/// </summary>
	public char PerspectiveColor { get; }

	/// <summary>
	///     Gets the controller kind for White.
	/// </summary>
	public MatchSideControllerKind WhiteController { get; }

	/// <summary>
	///     Gets the controller kind for Black.
	/// </summary>
	public MatchSideControllerKind BlackController { get; }

	/// <summary>
	///     Gets the single manual side for compatibility with human-versus-engine workflows.
	///     This member is only valid when exactly one side is manual.
	/// </summary>
	public char PlayerColor => ResolveSingleControllerColor(MatchSideControllerKind.Manual, nameof(PlayerColor));

	/// <summary>
	///     Gets the single engine-controlled side for compatibility with human-versus-engine workflows.
	///     This member is only valid when exactly one side is engine-controlled.
	/// </summary>
	public char EngineColor => ResolveSingleControllerColor(MatchSideControllerKind.Engine, nameof(EngineColor));

	/// <summary>
	///     Gets the current played-move history.
	/// </summary>
	public ImmutableArray<PlayedMove> MoveHistory
	{
		get
		{
            using var operation = EnterOperation();
			if (_moveHistorySnapshotDirty)
			{
				_moveHistorySnapshot = [.. _moveHistory];
				_moveHistorySnapshotDirty = false;
			}
			return _moveHistorySnapshot;
		}
	}

	/// <summary>
	///     Gets the current raw played move list.
	/// </summary>
	public ImmutableArray<string> PlayedMoves { get { using var operation = EnterOperation(); return [.. _playedMoves]; } }

	/// <summary>
	///     Gets the current refreshed match state.
	/// </summary>
	/// <exception cref="InvalidOperationException">Thrown when the position has not been refreshed yet.</exception>
	public PlayableMatchState CurrentState
    {
        get
        {
            using var operation = EnterOperation();
            return _hasCurrentState ? _currentState
                : throw new InvalidOperationException("The current position is not available until RefreshAsync has completed.");
        }
    }

	/// <summary>
	///     Gets the current pending promotion request, when applicable.
	/// </summary>
	public PendingPromotionRequest? PendingPromotion { get { using var operation = EnterOperation(); return _pendingPromotion; } }

	/// <summary>
	///     Tries to resolve a cached legal-move classification for the current position.
	/// </summary>
	public bool TryGetLegalMoveClassification(string move, out MoveClassification classification)
	{
        using var operation = EnterOperation();
		string normalizedMove = NormalizeCompletedMove(move);
		return GetCurrentLegalMoveClassifications().TryGetValue(normalizedMove, out classification);
	}

	/// <summary>
	///     Tries to resolve a played move's latest move classification.
	/// </summary>
	public bool TryGetPlayedMoveClassification(PlayedMove move, out MoveClassification classification)
	{
		var classifications = _classifications.GetKnown(move.ParentPositionKey);
		if (classifications.TryGetValue(move.Move, out classification))
			return true;

		classification = move.Classification;
		return classification.MovingPiece != '\0';
	}

	/// <summary>
	///     Tries to resolve a played move's high-quality score from the cached parent-position analysis.
	/// </summary>
	public bool TryGetPlayedMoveScore(PlayedMove move, out PositionScore score) =>
		move.TryResolveScore(ResolvePositionAnalysis, out score);

	/// <summary>
	///     Tries to resolve a cached full-strength analysis for the supplied position key.
	/// </summary>
	public bool TryGetPositionAnalysis(string positionKey, out PositionAnalysisResult analysis)
	{
		if (_positionAnalysis is not null)
			return _positionAnalysis.TryGetAnalysis(positionKey, out analysis);
		analysis = default;
		return false;
	}

	/// <summary>
	///     Gets the latest cached legal-move classifications for the current position.
	/// </summary>
	public ImmutableDictionary<string, MoveClassification> GetCurrentLegalMoveClassifications() =>
		_classifications.GetKnown(CurrentState.PositionKey);

	/// <summary>
	///     Resolves the current position advantage from the best completed cached analysis available so far.
	/// </summary>
	public PositionAdvantage ResolveCurrentAdvantage()
	{
        using var operation = EnterOperation();
		var state = CurrentState;
		return ResolveAdvantage(state.PositionKey, state.LegalMoves.Length);
	}

	/// <summary>
	///     Builds simple debugging lines for the played-move history using the best completed cached scores available so far.
	/// </summary>
	public string[] GetMoveHistoryDisplayLines()
    {
        using var operation = EnterOperation();
        return _moveHistory.ToDisplayLines(ResolvePlayedMoveScore);
    }

	/// <summary>
	///     Returns whether the requested number of played moves can currently be undone.
	/// </summary>
	/// <param name="count">Number of moves to undo.</param>
	/// <returns>
	///     <see langword="true" /> when at least <paramref name="count" /> moves have been played and, for a clocked
	///     match, the retained position has a clock checkpoint.
	/// </returns>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="count" /> is not greater than zero.</exception>
	public bool CanUndoMoves(int count = 1)
	{
        using var operation = EnterOperation();
		ValidatePositive(count, nameof(count));
		if (_playedMoves.Count < count)
			return false;

		return _clockHistory.Count == 0 || _playedMoves.Count - count >= _clockHistoryOriginMoveCount;
	}

	/// <summary>
	///     Undoes the requested number of played moves, preserving cached analysis and classifications for the retained
	///     move prefix while canceling obsolete background work. Retained clock values and move counts are restored, and
	///     the retained turn restarts unpaused at the undo time.
	/// </summary>
	/// <param name="count">Number of moves to undo.</param>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="count" /> is not greater than zero.</exception>
	/// <exception cref="InvalidOperationException">
	///     Thrown when fewer than <paramref name="count" /> moves have been played or the retained position predates the
	///     available clock history.
	/// </exception>
	public void UndoMoves(int count = 1)
	{
        using var operation = EnterOperation(observeClock: true);
		ThrowIfDisposed();
		ValidatePositive(count, nameof(count));
		if (_playedMoves.Count < count)
			throw new InvalidOperationException("Cannot undo more moves than have been played.");

		int remainingMoveCount = _playedMoves.Count - count;
		if (_clockHistory.Count > 0 && remainingMoveCount < _clockHistoryOriginMoveCount)
			throw new InvalidOperationException("Cannot undo beyond the available clock history.");

		if (_playedMoves.Count > remainingMoveCount)
			_playedMoves.RemoveRange(remainingMoveCount, _playedMoves.Count - remainingMoveCount);

		int retainedHistoryCount = Math.Min(_moveHistory.Count, remainingMoveCount);
		if (_moveHistory.Count > retainedHistoryCount)
		{
			_moveHistory.RemoveRange(retainedHistoryCount, _moveHistory.Count - retainedHistoryCount);
			_moveHistorySnapshotDirty = true;
		}

		if (_clockHistory.Count > 0)
		{
			int retainedCheckpointIndex = remainingMoveCount - _clockHistoryOriginMoveCount;
			if (_clockHistory.Count > retainedCheckpointIndex + 1)
				_clockHistory.RemoveRange(
					retainedCheckpointIndex + 1,
					_clockHistory.Count - (retainedCheckpointIndex + 1));

            _clockHistory[^1] = PlayableMatchClockKernel.RestartTurn(_clockHistory[^1], OperationUtc);
		}

		_positionRevision++;
		RewindRepetitionHistory(remainingMoveCount);
		_pendingPromotion = null;
		_forcedResult = default;
		_declaredDrawMove = null;
		_claimableResult = null;
		_drawOfferedBy = null;

		var retainedPositionKeys = CollectRetainedPositionKeys();
		DeferCancellation(_positionAnalysis?.BeginCancellation(retainedPositionKeys));
		DeferCancellation(_classifications.BeginCancellation(retainedPositionKeys));

		_hasCurrentState = false;
		_currentState    = default;
		RaiseEvent(PlayableMatchEventKind.MovesUndone, undoCount: count);
	}

	/// <summary>
	///     Loads a complete authored or saved match setup into the session and returns the refreshed state.
	/// </summary>
	/// <param name="setup">Complete match setup to load.</param>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>The refreshed match state after the setup is loaded.</returns>
	public Task<PlayableMatchState> LoadMatchAsync(PlayableMatchSetup setup, CancellationToken ct = default)
	{
        using var operation = EnterOperation(observeClock: true);
		LoadMatchCore(setup, ct);
		var state = Refresh();
		RaiseEvent(PlayableMatchEventKind.PositionLoaded, state: state);
		return Task.FromResult(state);
	}

	/// <summary>
	///     Captures the complete local game, including every available clock checkpoint needed for undo after a reload.
	///     Snapshot arrays are allocated only on this explicit request. Refreshes and adjudicates the current clock first.
	/// </summary>
	public PlayableMatchSave CaptureSave()
	{
        using var operation = EnterOperation(observeClock: true);
		var state = Refresh();
		var clocks = ImmutableArray.CreateBuilder<PlayableMatchClockRestore>(_clockHistory.Count);
		if (_timeControl.HasValue)
		{
			for (int index = 0; index < _clockHistory.Count; index++)
			{
				var checkpoint = _clockHistory[index];
				var snapshot = PlayableMatchClockKernel.SnapshotAtTurnStart(_timeControl.Value, checkpoint);
				clocks.Add(new(snapshot.WhiteRemaining, snapshot.BlackRemaining, snapshot.ActiveColor,
					snapshot.DelayRemaining, snapshot.IsPaused, checkpoint.WhiteMovesCompleted,
					checkpoint.BlackMovesCompleted, snapshot.ActiveStageIndex, snapshot.SnapshotUtc));
			}
		}
		PlayableMatchClockSetup? liveClock = null;
		if (state.Clock.HasValue)
		{
			var current = state.Clock.Value;
			var checkpoint = _clockHistory[^1];
			liveClock = PlayableMatchClockSetup.FromExactRestore(new(current.WhiteRemaining, current.BlackRemaining,
				current.ActiveColor, current.DelayRemaining, current.IsPaused, checkpoint.WhiteMovesCompleted,
				checkpoint.BlackMovesCompleted, current.ActiveStageIndex, current.SnapshotUtc));
		}
		return new(new(_baseFen, _playedMoves, liveClock), _timeControl, clocks.MoveToImmutable(),
			_clockHistoryOriginMoveCount, state.Result, _drawOfferedBy, _pendingPromotion?.MovePrefix,
			_claimableDrawPolicy, _drawOfferPolicy, _declaredDrawMove);
	}

	/// <summary>
	///     Restores a saved local match and its undo history after validating the whole save before replacing the game.
	/// </summary>
	/// <param name="save">Save produced by <see cref="CaptureSave" /> or authored with the same invariants.</param>
	/// <param name="countTimeSinceSave">Whether an unpaused clock includes time elapsed since saving. Defaults to resuming at load time.</param>
	/// <param name="ct">Cancellation before replacement leaves the existing game unchanged.</param>
	/// <remarks>The receiving session must use equal time-control and draw policies. Clock history may begin after move zero.</remarks>
	public Task<PlayableMatchState> LoadSaveAsync(
		PlayableMatchSave save, bool countTimeSinceSave = false, CancellationToken ct = default)
	{
        using var operation = EnterOperation(observeClock: true);
		ThrowIfDisposed();
		ct.ThrowIfCancellationRequested();
		var validated = PlayableMatchSaveValidator.ValidateAndResolve(save, _timeControl, _claimableDrawPolicy,
			_drawOfferPolicy, ct, !countTimeSinceSave && _timeControl.HasValue ? OperationUtc : null);
		ct.ThrowIfCancellationRequested();
		LoadMatchCore(save.Setup, ct);
		_clockHistory.Clear();
		_clockHistory.AddRange(validated.ClockHistory);
		_clockHistoryOriginMoveCount = save.ClockHistoryOriginMoveCount;
		_pendingPromotion = validated.PendingPromotion;
		_drawOfferedBy = save.DrawOfferedBy;
		_forcedResult = save.Result;
		_declaredDrawMove = save.DeclaredDrawMove;
		var state = Refresh();
		RaiseEvent(PlayableMatchEventKind.PositionLoaded, state: state);
		return Task.FromResult(state);
	}


	/// <summary>
	///     Loads an arbitrary base position and optional played-move sequence into the session.
	/// </summary>
	[Obsolete("Use LoadMatchAsync(new PlayableMatchSetup(baseFen, moves), ct) instead.")]
	public Task LoadPositionAsync(
		Fen                  baseFen,
		IEnumerable<string>? moves = null,
		CancellationToken    ct    = default)
	{
        using var operation = EnterOperation(observeClock: true);
		LoadMatchCore(new(baseFen, moves), ct);
		RaiseEvent(PlayableMatchEventKind.PositionLoaded, state: _hasCurrentState ? _currentState : null);
        return Task.CompletedTask;
	}

    private Task LoadPositionRequestAsync(PlayableMatchSetup setup, CancellationToken ct)
    {
        using var operation = EnterOperation(observeClock: true);
        LoadMatchCore(setup, ct);
        RaiseEvent(PlayableMatchEventKind.PositionLoaded, state: _hasCurrentState ? _currentState : null);
        return Task.CompletedTask;
    }

	private void LoadMatchCore(PlayableMatchSetup setup, CancellationToken ct)
	{
		ThrowIfDisposed();
		ct.ThrowIfCancellationRequested();
		var currentFen = PlayableMatchHistoryValidator.ValidateAndResolve(setup, _claimableDrawPolicy, ct);
		var clockRestore = CreateClockRestore(currentFen, setup.Clock);
		PlayableMatchClockCheckpoint? checkpoint = null;
		if (clockRestore.HasValue)
		{
			if (!_timeControl.HasValue)
				throw new InvalidOperationException("Cannot restore clock state because this session has no time control.");
			checkpoint = PlayableMatchClockKernel.Restore(_timeControl.Value, currentFen.ActiveColor, clockRestore.Value);
		}

		// Validate before replacing the local match. Engines synchronize only on explicit search requests.
		ct.ThrowIfCancellationRequested();

		_positionRevision++;
		_baseFen = setup.BaseFen;
		_playedMoves.Clear();
		_playedMoves.AddRange(setup.PlayedMoves);
		_moveHistory.Clear();
		_moveHistorySnapshot = [];
		_moveHistorySnapshotDirty = false;
		ResetRepetitionHistory();
		UpdateMoveHistory(currentFen.Raw);
		_pendingPromotion = null;
		_lastResult = default;
		_forcedResult = default;
		_declaredDrawMove = null;
		_claimableResult = null;
		_drawOfferedBy = null;
		if (checkpoint.HasValue)
		{
			_clockHistory.Clear();
			_clockHistory.Add(checkpoint.Value);
		}
		else
			InitializeClocks(currentFen.ActiveColor);
		_clockHistoryOriginMoveCount = _clockHistory.Count > 0 ? _playedMoves.Count : 0;
		ResetBackgroundState();
		_hasCurrentState = false;
		_currentState = default;
	}

	/// <summary>
	///     Starts a new game from the standard initial position.
	/// </summary>
	public Task StartNewGameAsync(CancellationToken ct = default)
	{
        using var operation = EnterOperation(observeClock: true);
		LoadMatchCore(PlayableMatchSetup.Standard, ct);
		RaiseEvent(PlayableMatchEventKind.PositionLoaded, state: _hasCurrentState ? _currentState : null);
		RaiseEvent(PlayableMatchEventKind.GameStarted);
        return Task.CompletedTask;
	}

	/// <summary>
	///     Plays the engine's next move using the configured engine move time.
	///     This compatibility alias is only valid when the current side is engine-controlled.
	/// </summary>
	/// <remarks>Use <see cref="PlayControlledMoveAsync" /> instead.</remarks>
	[Obsolete("Use PlayControlledMoveAsync instead.", false)]
	public Task<EngineMoveResult> PlayEngineMoveAsync(CancellationToken ct = default) => PlayControlledMoveAsync(ct);

	/// <summary>
	///     Plays the current side's move when that side is engine-controlled.
	/// </summary>
	public async Task<EngineMoveResult> PlayControlledMoveAsync(CancellationToken ct = default)
    {
        CancellationTokenSource cancellation;
        using (EnterOperation())
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();
            if (_engineTurnGate is null)
                throw new InvalidOperationException("No engine client is configured for controlled moves.");
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _engineLifetimeCancellation!.Token);
            _activeEngineTurns++;
        }
        bool entered = false;
        try
        {
            ct = cancellation.Token;
            await _engineTurnGate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            PlayableMatchState state;
            Fen baseFen;
            ImmutableArray<string> playedMoves;
            long revision;
            using (EnterOperation(observeClock: true))
            {
                EnsureMatchIsPlayable();
                EnsureTurnHasTimeRemaining(_timeControl.HasValue ? OperationUtc : null);
                state = CurrentState;
                if (GetController(state.Fen.ActiveColor) != MatchSideControllerKind.Engine)
                    throw new InvalidOperationException("The current side is not engine-controlled.");
                revision = _positionRevision;
                baseFen = _baseFen;
                playedMoves = [.. _playedMoves];
            }
            SearchResult result = default;
            string move;
            try
            {
                await _playingClient!.SetPositionAsync(baseFen, playedMoves, ct).ConfigureAwait(false);
                using (EnterOperation()) { ThrowIfDisposed(); EnsurePositionRevision(revision); }
                result = await _playingClient.GoAsync(new() { MoveTimeMs = _engineMoveTimeMs }, ct).ConfigureAwait(false);
                move = result.BestMove.ToLowerInvariant();
            }
            catch (InvalidOperationException)
            {
                if (_controlledMoveFallbackPolicy == PlayableMatchControlledMoveFallbackPolicy.Throw) throw;
                move = SelectFallbackControlledMove(state);
            }
            using (EnterOperation(observeClock: true))
            {
                ct.ThrowIfCancellationRequested();
                EnsurePositionRevision(revision);
                EnsureMatchIsPlayable();
                DateTimeOffset? completedAt = _timeControl.HasValue ? OperationUtc : null;
                EnsureTurnHasTimeRemaining(completedAt);
                if (!state.LegalMoves.ContainsUciMove(move))
                    throw new InvalidOperationException($"Engine produced '{move}', which is not legal in the current position.");
                CompleteMove(move, state.Fen.ActiveColor, completedAt);
            }
            return new(move, result);
        }
        finally
        {
            if (entered) _engineTurnGate.Release();
            cancellation.Dispose();
            using var operation = EnterOperation();
            _activeEngineTurns--;
            ReleaseEngineResourcesWhenIdle();
        }
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
		ThrowIfDisposed();
		var state = await RefreshAsync(ct).ConfigureAwait(false);
		if (state.Result.IsTerminal ||
			state.LegalMoves.IsDefaultOrEmpty ||
			GetController(state.Fen.ActiveColor) != MatchSideControllerKind.Engine)
		{
			return null;
		}

		return await PlayControlledMoveAsync(ct).ConfigureAwait(false);
	}

	/// <summary>
	///     Waits for tactical move classifications to complete for the current position.
	/// </summary>
	public Task<ImmutableDictionary<string, MoveClassification>> WaitForCurrentMoveClassificationsAsync(
		CancellationToken ct = default)
	{
        using var operation = EnterOperation();
		ThrowIfDisposed();
		ct.ThrowIfCancellationRequested();
		var state = CurrentState;
		_classifications.Enqueue(state.PositionKey, state.Fen, state.LegalMoves);
		return _classifications.WaitAsync(state.PositionKey, ct);
	}

	/// <summary>
	///     Refreshes the current board snapshot, legal moves, live classifications, and non-blocking player-relative
	///     advantage.
	/// </summary>
	public Task<PlayableMatchState> RefreshAsync(CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		return Task.FromResult(Refresh());
	}

	/// <summary>
	///     Refreshes the match synchronously, without allocating a task, contacting an engine, or starting analysis.
	/// </summary>
	public PlayableMatchState Refresh()
	{
        using var operation = EnterOperation(observeClock: true);
		ThrowIfDisposed();
		if (_timeControl.HasValue && _clockHistory.Count == 0)
		{
			InitializeClocks(BuildCurrentFen().ActiveColor);
			_clockHistoryOriginMoveCount = _playedMoves.Count;
		}
		return RefreshLocalState(BuildCurrentFen());
	}

	private PlayableMatchState RefreshLocalState(Fen currentFen)
	{
		var legalMoves = GetCachedLegalMoves(currentFen);
		string positionKey = currentFen.Raw;

		UpdateMoveHistory(positionKey);
		long classificationsVersion = _classifications.KnownVersion;
		if (classificationsVersion != _enrichedClassificationVersion)
		{
			if (classificationsVersion != 0)
				EnrichMoveHistoryClassifications();
			_enrichedClassificationVersion = classificationsVersion;
		}

		var advantage = ResolveAdvantage(positionKey, legalMoves.Length);

		var classifications = _classifications.GetKnown(positionKey);
		var clock = GetClockSnapshot();
		var outcome = EvaluateOutcome(currentFen, legalMoves, clock);
		_claimableResult = outcome.ClaimableResult;
		var currentResult = outcome.Result;
		if (currentResult.IsTerminal)
		{
			_forcedResult = currentResult;
			_pendingPromotion = null;
			_drawOfferedBy = null;
			FreezeClock(clock?.SnapshotUtc);
			if (clock.HasValue)
				clock = clock.Value with { IsPaused = true };
		}
		_currentState = new(
			currentFen,
			positionKey,
			legalMoves,
			classifications,
			advantage,
			MoveHistory,
			_pendingPromotion,
			currentResult,
			outcome.ClaimableResult,
			_drawOfferedBy,
			clock
		);
		_hasCurrentState = true;
		RaiseEvent(PlayableMatchEventKind.PositionRefreshed, state: _currentState);
		if (_lastResult != currentResult)
		{
			_lastResult = currentResult;
			if (currentResult.IsTerminal)
				RaiseEvent(PlayableMatchEventKind.ResultChanged, state: _currentState, result: currentResult);
		}

		return _currentState;
	}

	/// <summary>
	///     Returns the full-strength legal-move analysis for the current position, enriched with any known move
	///     classifications.
	/// </summary>
	public async Task<PositionAnalysisResult> GetLegalMoveAnalysisAsync(CancellationToken ct = default)
    {
        long revision;
        string positionKey;
        Task<PositionAnalysisResult> pending;
        using (EnterOperation())
        {
            ThrowIfDisposed();
            if (_positionAnalysis is null)
                throw new InvalidOperationException("No analysis engine is configured for this local session.");
            ct.ThrowIfCancellationRequested();
            revision = _positionRevision;
            var state = CurrentState;
            positionKey = state.PositionKey;
            _positionAnalysis.Enqueue(positionKey, _baseFen, _playedMoves, state.Fen.ActiveColor, PerspectiveColor, state.LegalMoves);
            pending = _positionAnalysis.GetAnalysisAsync(positionKey, ct);
        }
        var analysis = await pending.ConfigureAwait(false);
        using (EnterOperation()) { ThrowIfDisposed(); EnsurePositionRevision(revision); }
        var classifications = _classifications.GetKnown(positionKey);
        if (analysis.Evaluations.IsDefaultOrEmpty || classifications.Count == 0) return analysis;
        var evaluations = analysis.Evaluations.Select(evaluation =>
            classifications.TryGetValue(evaluation.Move, out var classification)
                ? evaluation with { Classification = classification } : evaluation).ToImmutableArray();
        return new(analysis.Advantage, evaluations);
    }

	/// <summary>
	///     Applies a validated human move to the current match state.
	///     This compatibility alias is only valid when the current side is manually controlled.
	/// </summary>
	/// <remarks>Use <see cref="ApplyMove" /> instead.</remarks>
	[Obsolete("Use ApplyMove instead.", false)]
	public void ApplyHumanMove(string move) => ApplyMove(move);

	/// <summary>
	///     Applies a validated move for the current side when that side is externally controlled.
	/// </summary>
	public void ApplyMove(string move)
	{
        using var operation = EnterOperation(observeClock: true);
		EnsureMatchIsPlayable();
		if (_pendingPromotion.HasValue)
			throw new InvalidOperationException("Choose a promotion piece before applying another move.");

		DateTimeOffset? moveTimestamp = _timeControl.HasValue ? OperationUtc : null;
		EnsureTurnHasTimeRemaining(moveTimestamp);

		string normalizedMove = NormalizeMoveOrPrefix(move);
		var state = CurrentState;

		if (GetController(state.Fen.ActiveColor) == MatchSideControllerKind.Engine)
			throw new InvalidOperationException("A manual move cannot be applied on an engine-controlled turn.");

		if (state.LegalMoves.ContainsUciMove(normalizedMove))
		{
			CompleteMove(normalizedMove, state.Fen.ActiveColor, moveTimestamp);
			return;
		}

		if (LocalPositionRules.TryCreatePendingPromotion(state.Fen, normalizedMove, state.LegalMoves, out var request))
		{
			_pendingPromotion = request;
			UpdateCurrentStateMetadata();
			RaiseEvent(PlayableMatchEventKind.PromotionRequired, pendingPromotion: request);
			return;
		}

		RaiseEvent(
			PlayableMatchEventKind.IllegalMoveRejected,
			state: state,
			move: normalizedMove,
			error: "The move is not legal in the current position."
		);
		throw new InvalidOperationException("The move is not legal in the current position.");
	}

	/// <summary>
	///     Completes the current pending promotion using the supplied lowercase promotion suffix.
	/// </summary>
	/// <param name="promotionPiece">Promotion suffix such as <c>q</c>, <c>r</c>, <c>b</c>, or <c>n</c>.</param>
	public void ChoosePromotion(char promotionPiece)
	{
        using var operation = EnterOperation(observeClock: true);
		ThrowIfDisposed();
		if (!_pendingPromotion.HasValue)
			throw new InvalidOperationException("There is no pending promotion to complete.");

		EnsureMatchIsPlayable();
		DateTimeOffset? completedAt = _timeControl.HasValue ? OperationUtc : null;
		EnsureTurnHasTimeRemaining(completedAt);
		char normalizedPiece = char.ToLowerInvariant(promotionPiece);
		var request = _pendingPromotion.Value;
		if (!request.AllowedPromotionPieces.Contains(normalizedPiece))
			throw new InvalidOperationException("The requested promotion piece is not legal in the current position.");

		_pendingPromotion = null;
		string move = request.MovePrefix + normalizedPiece;
		RaiseEvent(PlayableMatchEventKind.PromotionChosen, move: move);
		CompleteMove(move, request.MovingSide, completedAt);
	}

	/// <summary>
	///     Dismisses an uncompleted promotion so the player can select another legal move. The board, turn and clock
	///     continue unchanged. Returns false when there is no pending choice; a terminal game cannot be reopened.
	/// </summary>
	public bool CancelPendingPromotion()
	{
        using var operation = EnterOperation(observeClock: true);
		ThrowIfDisposed();
		if (!_pendingPromotion.HasValue)
			return false;
		EnsureMatchIsPlayable();
		EnsureTurnHasTimeRemaining(_timeControl.HasValue ? OperationUtc : null);
		_pendingPromotion = null;
		UpdateCurrentStateMetadata();
		RaiseEvent(PlayableMatchEventKind.PromotionCanceled, state: _hasCurrentState ? _currentState : null);
		return true;
	}

	/// <summary>
	///     Offers a draw from the current side.
	/// </summary>
	public void OfferDraw()
	{
        using var operation = EnterOperation(observeClock: true);
		ThrowIfDisposed();
		OfferDraw(CurrentState.Fen.ActiveColor);
	}

	/// <summary>Offers a draw on behalf of either side, including while the opponent is thinking.</summary>
	public void OfferDraw(char offeringSide)
	{
        using var operation = EnterOperation(observeClock: true);
		char side = NormalizeColor(offeringSide, nameof(offeringSide));
		EnsureMatchIsPlayable();
		EnsureTurnHasTimeRemaining(_timeControl.HasValue ? OperationUtc : null);
		if (_drawOfferedBy.HasValue && _drawOfferedBy != side)
			throw new InvalidOperationException("Respond to the opposing draw offer before making another offer.");
		_drawOfferedBy = side;
		UpdateCurrentStateMetadata();
		RaiseEvent(PlayableMatchEventKind.DrawOffered, state: _hasCurrentState ? _currentState : null);
	}

	/// <summary>
	///     Accepts a pending draw offer from the opposing side.
	/// </summary>
	public void AcceptDraw()
	{
        using var operation = EnterOperation(observeClock: true);
		EnsureMatchIsPlayable();
		DateTimeOffset? completedAt = _timeControl.HasValue ? OperationUtc : null;
		EnsureTurnHasTimeRemaining(completedAt);
		if (!_drawOfferedBy.HasValue)
			throw new InvalidOperationException("There is no opposing draw offer to accept.");

		_drawOfferedBy = null;
		SetForcedResult(new(PlayableMatchResultReason.DrawAgreement, null), completedAt);
	}

	/// <summary>Accepts a draw only when the supplied side is the recipient of the pending offer.</summary>
	public void AcceptDraw(char acceptingSide)
	{
        using var operation = EnterOperation(observeClock: true);
		EnsureDrawRecipient(acceptingSide, nameof(acceptingSide));
		AcceptDraw();
	}

	/// <summary>
	///     Declines a pending draw offer from the opposing side.
	/// </summary>
	public void DeclineDraw()
	{
        using var operation = EnterOperation(observeClock: true);
		EnsureMatchIsPlayable();
		if (!_drawOfferedBy.HasValue)
			throw new InvalidOperationException("There is no opposing draw offer to decline.");

		_drawOfferedBy = null;
		UpdateCurrentStateMetadata();
		RaiseEvent(PlayableMatchEventKind.DrawDeclined, state: _hasCurrentState ? _currentState : null);
	}

	/// <summary>Declines a draw only when the supplied side is the recipient of the pending offer.</summary>
	public void DeclineDraw(char decliningSide)
	{
        using var operation = EnterOperation(observeClock: true);
		EnsureDrawRecipient(decliningSide, nameof(decliningSide));
		DeclineDraw();
	}

	private void EnsureDrawRecipient(char respondingSide, string parameterName)
	{
		ThrowIfDisposed();
		char side = NormalizeColor(respondingSide, parameterName);
		if (!_drawOfferedBy.HasValue || _drawOfferedBy == side)
			throw new InvalidOperationException("Only the recipient of a pending draw offer can respond to it.");
	}

	/// <summary>
	///     Claims the current claimable draw result when explicit draw claims are enabled.
	/// </summary>
	public void ClaimDraw()
	{
        using var operation = EnterOperation(observeClock: true);
		EnsureMatchIsPlayable();
		DateTimeOffset? completedAt = _timeControl.HasValue ? OperationUtc : null;
		EnsureTurnHasTimeRemaining(completedAt);
		if (!_claimableResult.HasValue || !_claimableResult.Value.IsDraw)
			throw new InvalidOperationException("There is no claimable draw available.");

		SetForcedResult(_claimableResult.Value, completedAt);
	}

	/// <summary>
	///     Checks whether a legal intended move would permit a fifty-move or threefold-repetition draw claim.
	///     The move is inspected without changing the board, clocks, history or cached analysis.
	/// </summary>
	/// <remarks>Returns false for an ended match or while promotion is pending. Invalid UCI notation throws.</remarks>
	public bool CanClaimDraw(string intendedMove)
	{
        using var operation = EnterOperation(observeClock: true);
		ThrowIfDisposed();
		string move = NormalizeCompletedMove(intendedMove);
		return !_forcedResult.IsTerminal && !CurrentState.Result.IsTerminal && !_pendingPromotion.HasValue &&
			ResolveIntendedDrawClaim(move).HasValue;
	}

	/// <summary>
	///     Claims a draw by declaring a legal intended move under FIDE articles 9.2.1.1 and 9.3.1.
	///     The declared move is not executed. No increment is awarded and the clock stops at claim time.
	/// </summary>
	public void ClaimDraw(string intendedMove)
	{
        using var operation = EnterOperation(observeClock: true);
		EnsureMatchIsPlayable();
		DateTimeOffset? completedAt = _timeControl.HasValue ? OperationUtc : null;
		EnsureTurnHasTimeRemaining(completedAt);
		if (_pendingPromotion.HasValue)
			throw new InvalidOperationException("Choose a promotion piece before claiming an intended move.");
		var result = ResolveIntendedDrawClaim(NormalizeCompletedMove(intendedMove));
		if (!result.HasValue)
			throw new InvalidOperationException("The intended move does not permit a draw claim.");
		SetForcedResult(result.Value, completedAt, NormalizeCompletedMove(intendedMove));
	}

	private PlayableMatchResult? ResolveIntendedDrawClaim(string move)
	{
		var state = CurrentState;
		if (!state.LegalMoves.ContainsUciMove(move))
			return null;
		var next = state.Fen.ApplyMove(move);
		if (next.HalfmoveClock >= 100)
			return new(PlayableMatchResultReason.FiftyMoveRule, null);
		string key = LocalPositionRules.BuildRepetitionKey(next);
		return _repetitionCounts.TryGetValue(key, out int previousOccurrences) && previousOccurrences >= 2
			? new(PlayableMatchResultReason.ThreefoldRepetition, null)
			: null;
	}

	/// <summary>
	///     Resigns on behalf of the current side.
	/// </summary>
	public void Resign()
	{
        using var operation = EnterOperation(observeClock: true);
		ThrowIfDisposed();
		Resign(CurrentState.Fen.ActiveColor);
	}

	/// <summary>
	///     Resigns on behalf of either side, including off turn. The game is drawn if the opponent has no possible mate
	///     under the conservative material test used for timeout adjudication (FIDE article 5.1.2).
	/// </summary>
	public void Resign(char resigningSide)
	{
        using var operation = EnterOperation(observeClock: true);
		char side = NormalizeColor(resigningSide, nameof(resigningSide));
		EnsureMatchIsPlayable();
		DateTimeOffset? completedAt = _timeControl.HasValue ? OperationUtc : null;
		EnsureTurnHasTimeRemaining(completedAt);
		var state = CurrentState;
		char opponent = Opposite(side);
		SetForcedResult(new(PlayableMatchResultReason.Resignation,
			LocalPositionRules.CanPossiblyMate(state.Fen, opponent) ? opponent : null), completedAt);
	}

	/// <summary>
	///     Pauses the active match clock.
	/// </summary>
	public void PauseClock()
	{
        using var operation = EnterOperation(observeClock: true);
		ThrowIfDisposed();
        if (!_timeControl.HasValue || _clockHistory.Count == 0 || _clockHistory[^1].PausedAtUtc.HasValue)
			return;

        _clockHistory[^1] = PlayableMatchClockKernel.Pause(_clockHistory[^1], OperationUtc);
		UpdateCurrentStateMetadata();
		RaiseEvent(PlayableMatchEventKind.ClockPaused, state: _hasCurrentState ? _currentState : null);
	}

	/// <summary>
	///     Resumes the active match clock.
	/// </summary>
	public void ResumeClock()
	{
        using var operation = EnterOperation(observeClock: true);
		ThrowIfDisposed();
        if (_forcedResult.IsTerminal || !_timeControl.HasValue || _clockHistory.Count == 0 || !_clockHistory[^1].PausedAtUtc.HasValue)
			return;

        _clockHistory[^1] = PlayableMatchClockKernel.Resume(_clockHistory[^1], OperationUtc);
		UpdateCurrentStateMetadata();
		RaiseEvent(PlayableMatchEventKind.ClockResumed, state: _hasCurrentState ? _currentState : null);
	}

	/// <summary>
	///     Cancels any in-flight background analysis or classification.
	/// </summary>
	public void CancelAnalysis()
	{
        using var operation = EnterOperation();
		ThrowIfDisposed();
		DeferCancellation(_positionAnalysis?.BeginCancellation());
		DeferCancellation(_classifications.BeginCancellation());
	}

	/// <summary>
	///     Processes a serializable request DTO against the session.
	/// </summary>
	/// <param name="request">Request to process.</param>
	/// <param name="ct">Cancellation token.</param>
	public async Task ProcessAsync(PlayableMatchRequest request, CancellationToken ct = default)
	{
		ThrowIfDisposed();
		ct.ThrowIfCancellationRequested();
		if (request.SchemaVersion != 1)
			throw new InvalidOperationException($"Unsupported playable match request schema version '{request.SchemaVersion}'.");

		switch (request.Kind)
		{
			case PlayableMatchRequestKind.StartNewGame:
				await StartNewGameAsync(ct).ConfigureAwait(false);
				return;
			case PlayableMatchRequestKind.LoadPosition:
				if (!request.BaseFen.HasValue)
					throw new InvalidOperationException("A base FEN is required for a load-position request.");

				await LoadPositionRequestAsync(new(request.BaseFen.Value, request.Moves), ct).ConfigureAwait(false);
				return;
			case PlayableMatchRequestKind.Refresh:
				await RefreshAsync(ct).ConfigureAwait(false);
				return;
			case PlayableMatchRequestKind.ApplyMove:
				ApplyMove(request.Move ?? throw new InvalidOperationException("Move is required for apply-move."));
				return;
			case PlayableMatchRequestKind.ChoosePromotion:
				if (!request.PromotionPiece.HasValue)
					throw new InvalidOperationException("Promotion piece is required for choose-promotion.");

				ChoosePromotion(request.PromotionPiece.Value);
				return;
			case PlayableMatchRequestKind.CancelPendingPromotion:
				CancelPendingPromotion();
				return;
			case PlayableMatchRequestKind.PlayControlledMove:
				await PlayControlledMoveAsync(ct).ConfigureAwait(false);
				return;
			case PlayableMatchRequestKind.UndoMoves:
				UndoMoves(request.UndoCount);
				return;
			case PlayableMatchRequestKind.OfferDraw:
				OfferDraw();
				return;
			case PlayableMatchRequestKind.AcceptDraw:
				AcceptDraw();
				return;
			case PlayableMatchRequestKind.DeclineDraw:
				DeclineDraw();
				return;
			case PlayableMatchRequestKind.ClaimDraw:
				if (request.Move is null) ClaimDraw();
				else ClaimDraw(request.Move);
				return;
			case PlayableMatchRequestKind.Resign:
				Resign();
				return;
			case PlayableMatchRequestKind.PauseClock:
				PauseClock();
				return;
			case PlayableMatchRequestKind.ResumeClock:
				ResumeClock();
				return;
			case PlayableMatchRequestKind.CancelAnalysis:
				CancelAnalysis();
				return;
			default:
				throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown playable match request.");
		}
	}

	/// <summary>
	///     Processes a batch of serializable requests and returns the ordered canonical events emitted while doing so.
	/// </summary>
	/// <param name="requests">Requests to process in order.</param>
	/// <param name="ct">Cancellation token.</param>
	public async Task<ImmutableArray<PlayableMatchEvent>> ProcessBatchAsync(
		IEnumerable<PlayableMatchRequest> requests,
		CancellationToken                 ct = default)
	{
		ThrowIfDisposed();
		if (requests is null)
			throw new ArgumentNullException(nameof(requests));

        AsyncLocal<BatchCapture?> context;
        using (EnterOperation())
        {
            ThrowIfDisposed();
            context = _batchEvents ??= new();
        }
        var capture = new BatchCapture();
        var previous = context.Value;
        context.Value = capture;
        try
        {
            foreach (var request in requests)
            {
                ct.ThrowIfCancellationRequested();
                await ProcessAsync(request, ct).ConfigureAwait(false);
            }
            using var operation = EnterOperation();
            return [.. capture.Events];
        }
        finally
        {
            using (EnterOperation()) { capture.IsActive = false; capture.Events.Clear(); }
            context.Value = previous;
        }
	}

	/// <summary>
	///     Returns the configured controller kind for the supplied side.
	/// </summary>
	/// <param name="side">The side to inspect: <c>w</c> or <c>b</c>.</param>
	public MatchSideControllerKind GetController(char side) => NormalizeColor(side, nameof(side)) switch
	{
		'w' => WhiteController,
		_ => BlackController
	};

	private static char NormalizeColor(char color, string paramName)
	{
		if (color is 'w' or 'b')
			return color;

		throw new ArgumentOutOfRangeException(paramName, "Color must be 'w' or 'b'.");
	}

	private static int ValidatePositive(int value, string paramName)
	{
		if (value > 0)
			return value;

		throw new ArgumentOutOfRangeException(paramName, "Value must be greater than zero.");
	}

	private char ResolveSingleControllerColor(MatchSideControllerKind controller, string memberName)
	{
		if (WhiteController == controller && BlackController != controller)
			return 'w';

		if (BlackController == controller && WhiteController != controller)
			return 'b';

		throw new InvalidOperationException(
			$"{memberName} is only available when exactly one side uses the {controller} controller."
		);
	}

	private static string NormalizeCompletedMove(string move)
	{
		if (move is null)
			throw new ArgumentNullException(nameof(move));

		string normalizedMove = move.Trim().ToLowerInvariant();
		if (!UciEngineClient.IsUciMoveString(normalizedMove))
			throw new ArgumentException("Enter a move in UCI notation such as e2e4 or a7a8q.", nameof(move));

		return normalizedMove;
	}

	private static string NormalizeMoveOrPrefix(string move)
	{
		if (move is null)
			throw new ArgumentNullException(nameof(move));

		string normalizedMove = move.Trim().ToLowerInvariant();
		if (normalizedMove.Length is not 4 and not 5 ||
			!UciEngineClient.IsUciMoveString(
				normalizedMove.Length == 4 ? normalizedMove + "q" : normalizedMove
			))
		{
			throw new ArgumentException("Enter a move in UCI notation such as e2e4 or a7a8q.", nameof(move));
		}

		return normalizedMove;
	}

	private void EnrichMoveHistoryClassifications()
	{
		for (var i = 0; i < _moveHistory.Count; i++)
		{
			var move = _moveHistory[i];
			var classifications = _classifications.GetKnown(move.ParentPositionKey);
			if (!classifications.TryGetValue(move.Move, out var classification))
				continue;

			if (move.Classification == classification)
				continue;

			_moveHistory[i] = move with { Classification = classification };
			_moveHistorySnapshotDirty = true;
		}
	}

	private void ResetBackgroundState()
	{
		DeferCancellation(_positionAnalysis?.BeginCancellation());
		DeferCancellation(_classifications.BeginCancellation());
	}

	private HashSet<string> CollectRetainedPositionKeys()
	{
		var retainedPositionKeys = new HashSet<string>(StringComparer.Ordinal) { _baseFen.Raw };
		foreach (var move in _moveHistory)
		{
			retainedPositionKeys.Add(move.ParentPositionKey);
			retainedPositionKeys.Add(move.PositionKey);
		}

		return retainedPositionKeys;
	}

	private void UpdateMoveHistory(string currentPositionKey)
	{
		if (_repetitionKeys.Count == 0)
			ResetRepetitionHistory();
		if (_moveHistory.Count == _playedMoves.Count)
			return;

		if (_moveHistory.Count > _playedMoves.Count)
			throw new InvalidOperationException("Move history contains more entries than the played-move list.");

		var current = _moveHistory.Count == 0
			? _baseFen
			: Fen.Parse(_moveHistory[^1].PositionKey)!.Value;

		for (int moveIndex = _moveHistory.Count; moveIndex < _playedMoves.Count; moveIndex++)
		{
			string move = _playedMoves[moveIndex];
			string parentPositionKey = current.Raw;
			var classification = _classifications.GetKnown(parentPositionKey)
				.TryGetValue(move, out var knownClassification)
				? knownClassification
				: MoveClassification.Unknown();
			char movingSide = current.ActiveColor;
			int ply = current.FullmoveNumber;
			current = current.ApplyMove(move);
			AddRepetition(current);

			_moveHistory.Add(
				new(
					ply,
					movingSide,
					move,
					parentPositionKey,
					current.Raw,
					classification
				)
			);
			_moveHistorySnapshotDirty = true;
		}

		if (_moveHistory.Count > 0 && _moveHistory[^1].PositionKey != currentPositionKey)
			throw new InvalidOperationException("Move history does not match the current position snapshot.");
	}

	private Fen BuildCurrentFen()
	{
		if (_moveHistory.Count == _playedMoves.Count)
		{
			if (_moveHistory.Count == 0)
				return _baseFen;
			string key = _moveHistory[^1].PositionKey;
			return _cachedLegalMovesFen.HasValue && _cachedLegalMovesFen.Value.Raw == key
				? _cachedLegalMovesFen.Value : Fen.Parse(key)!.Value;
		}

		var current = _baseFen;
		foreach (string move in _playedMoves)
			current = current.ApplyMove(move);

		return current;
	}

	private void CompleteMove(string move, char movingSide, DateTimeOffset? completedAtUtc = null)
	{
		var previousFen = CurrentState.Fen;
		var resultingFen = LocalPositionRules.ApplyAndClassifyMove(previousFen, move, out var classification, out var legalMoves);
		_cachedLegalMovesFen = resultingFen;
		_cachedLegalMoves = legalMoves;
		var moveData = BuildMoveData(previousFen, resultingFen, move, classification);

		AdvanceClockForCompletedMove(movingSide, completedAtUtc);
		_positionRevision++;
		_playedMoves.Add(move);
		_moveHistory.Add(new(previousFen.FullmoveNumber, movingSide, move, previousFen.Raw, resultingFen.Raw, classification));
		_moveHistorySnapshotDirty = true;
		AddRepetition(resultingFen);
		if (_drawOfferPolicy == PlayableMatchDrawOfferPolicy.ExpireOnMove && _drawOfferedBy != movingSide)
			_drawOfferedBy = null;
		_forcedResult = default;
		_declaredDrawMove = null;
        PlayableMatchClockState? clock = _timeControl.HasValue && _clockHistory.Count > 0
            ? PlayableMatchClockKernel.SnapshotAtTurnStart(_timeControl.Value, _clockHistory[^1])
            : null;
        var immediateOutcome = EvaluateOutcome(resultingFen, legalMoves, clock);
		_claimableResult = immediateOutcome.ClaimableResult;
		_lastResult = immediateOutcome.Result;
		if (immediateOutcome.Result.IsTerminal)
		{
			_forcedResult = immediateOutcome.Result;
			_pendingPromotion = null;
			_drawOfferedBy = null;
			FreezeClock(clock?.SnapshotUtc);
		}
		_hasCurrentState = false;
		_currentState = default;
		RaiseEvent(PlayableMatchEventKind.MoveApplied, move: move, moveData: moveData);
		if (immediateOutcome.Result.IsTerminal)
			RaiseEvent(PlayableMatchEventKind.ResultChanged, move: move, moveData: moveData, result: immediateOutcome.Result);
	}

	private PlayableMatchClockRestore? CreateClockRestore(Fen currentFen, PlayableMatchClockSetup? setup)
	{
		if (!setup.HasValue)
			return null;
        if (!_timeControl.HasValue)
            throw new InvalidOperationException("Cannot restore clock state because this session has no time control.");

		var clockSetup = setup.Value;
        DateTimeOffset now = clockSetup.SnapshotUtc ?? OperationUtc;
        return PlayableMatchClockKernel.CreateRestore(currentFen, clockSetup, _timeControl, now);
	}

	private void InitializeClocks(char activeColor)
	{
		_clockHistory.Clear();
		if (!_timeControl.HasValue)
			return;

        _clockHistory.Add(PlayableMatchClockKernel.Initialize(_timeControl.Value, activeColor, OperationUtc));
	}

	private void AdvanceClockForCompletedMove(char movingSide, DateTimeOffset? completedAtUtc = null)
	{
		if (!_timeControl.HasValue || _clockHistory.Count == 0)
			return;

		DateTimeOffset now = completedAtUtc ?? OperationUtc;
        _clockHistory.Add(PlayableMatchClockKernel.CompleteMove(_timeControl.Value, _clockHistory[^1], movingSide, now));
	}

	private PlayableMatchClockState? GetClockSnapshot(DateTimeOffset? snapshotUtc = null)
	{
		if (!_timeControl.HasValue || _clockHistory.Count == 0)
			return null;

		DateTimeOffset now = snapshotUtc ?? OperationUtc;
        return PlayableMatchClockKernel.Snapshot(_timeControl.Value, _clockHistory[^1], now);
	}

	private void EnsureMatchIsPlayable()
	{
		ThrowIfDisposed();
		if (_forcedResult.IsTerminal || CurrentState.Result.IsTerminal)
			throw new InvalidOperationException("The match has already ended.");
	}

	private void EnsurePositionRevision(long revision)
	{
		if (_positionRevision != revision)
			throw new InvalidOperationException("The match position changed while the operation was in progress.");
	}

	private void FreezeClock(DateTimeOffset? now = null)
	{
		if (_timeControl.HasValue && _clockHistory.Count > 0 && !_clockHistory[^1].PausedAtUtc.HasValue)
			_clockHistory[^1] = PlayableMatchClockKernel.Pause(_clockHistory[^1], now ?? OperationUtc);
	}

	private void EnsureTurnHasTimeRemaining(DateTimeOffset? snapshotUtc = null)
	{
		if (_timeControl.HasValue && _timeControl.Value.TimeoutPolicy == PlayableMatchTimeoutPolicy.Ignore)
			return;

		var clock = GetClockSnapshot(snapshotUtc);
		if (!clock.HasValue)
			return;

		TimeSpan currentRemaining = clock.Value.ActiveColor == 'w'
			? clock.Value.WhiteRemaining
			: clock.Value.BlackRemaining;
		if (currentRemaining > TimeSpan.Zero)
			return;

		char opponent = Opposite(CurrentState.Fen.ActiveColor);
		SetForcedResult(new(PlayableMatchResultReason.Timeout,
			LocalPositionRules.CanPossiblyMate(CurrentState.Fen, opponent) ? opponent : null), clock.Value.SnapshotUtc);
		throw new InvalidOperationException("The current side has already lost on time.");
	}

	private MatchOutcome EvaluateOutcome(
		Fen fen, ImmutableArray<string> legalMoves, PlayableMatchClockState? clock)
	{
		if (_forcedResult.IsTerminal)
			return new(_forcedResult, null);
		var outcome = PlayableMatchAdjudication.Evaluate(fen, legalMoves.Length, clock, _timeControl,
			CountRepetitions(fen), _claimableDrawPolicy);
		return new(outcome.Result, outcome.ClaimableResult);
	}

	private ImmutableArray<string> GetCachedLegalMoves(Fen fen)
	{
		if (_cachedLegalMovesFen.HasValue && _cachedLegalMovesFen.Value.Raw == fen.Raw)
			return _cachedLegalMoves;
		_cachedLegalMovesFen = fen;
		_cachedLegalMoves = fen.GetLegalMoves();
		return _cachedLegalMoves;
	}

	private void ResetRepetitionHistory()
	{
		_repetitionCounts.Clear();
		_repetitionKeys.Clear();
		AddRepetition(_baseFen);
	}

	private void AddRepetition(Fen fen)
	{
		string key = LocalPositionRules.BuildRepetitionKey(fen);
		_repetitionKeys.Add(key);
		_repetitionCounts.TryGetValue(key, out int count);
		_repetitionCounts[key] = count + 1;
	}

	private void RewindRepetitionHistory(int retainedMoveCount)
	{
		for (int i = _repetitionKeys.Count - 1; i > retainedMoveCount; i--)
		{
			string key = _repetitionKeys[i];
			if (_repetitionCounts[key] == 1)
				_repetitionCounts.Remove(key);
			else
				_repetitionCounts[key]--;
			_repetitionKeys.RemoveAt(i);
		}
	}

	private int CountRepetitions(Fen currentFen)
	{
		string key = _repetitionKeys.Count > 0
			? _repetitionKeys[^1]
			: LocalPositionRules.BuildRepetitionKey(currentFen);
		return _repetitionCounts.TryGetValue(key, out int count) ? count : 0;
	}


	private PositionAnalysisResult? ResolvePositionAnalysis(string positionKey) =>
		TryGetPositionAnalysis(positionKey, out var analysis) ? analysis : null;

	private PositionAdvantage ResolveAdvantage(string positionKey, int legalMoveCount)
	{
		if (_positionAnalysis is null)
			return legalMoveCount == 0 ? PositionAdvantage.GameOver()
				: _moveHistory.Count == 0 ? PositionAdvantage.GameStart() : PositionAdvantage.Pending();
		return _moveHistory.ResolveCurrentAdvantage(positionKey, legalMoveCount,
			_positionAnalysisResolver ??= ResolvePositionAnalysis);
	}

	private PositionScore? ResolvePlayedMoveScore(PlayedMove move) =>
		TryGetPlayedMoveScore(move, out var score) ? score : null;

	private void RaiseEvent(
		PlayableMatchEventKind   kind,
		PlayableMatchState?      state            = null,
		string?                  move             = null,
		PlayableMatchMoveData?   moveData         = null,
		PlayableMatchResult?     result           = null,
		PendingPromotionRequest? pendingPromotion = null,
		int?                     undoCount        = null,
		string?                  error            = null)
	{
        var handlers = _eventSnapshot;
        var capture = _batchEvents?.Value;
        if (handlers.Length == 0 && capture is not { IsActive: true }) return;
        var matchEvent = new PlayableMatchEvent(kind, DateTimeOffset.UtcNow, state, move, moveData, result,
            pendingPromotion, undoCount, error);
        if (capture is { IsActive: true }) capture.Events.Add(matchEvent);
        if (handlers.Length != 0) (_pendingEvents ??= new List<PendingEvent>(2)).Add(new(handlers, matchEvent));
    }

    private DateTimeOffset OperationUtc => _operationUtc
        ?? throw new InvalidOperationException("Clock access requires a captured operation observation.");

    private OperationScope EnterOperation(bool observeClock = false)
    {
        DateTimeOffset? observed = null;
        bool needsObservation = observeClock && _timeControl.HasValue && !Monitor.IsEntered(_stateGate);
        if (needsObservation)
        {
            // An observation obtained before another clock command is admitted may be stale.
            // Retry outside the monitor; the injected clock is never called under the state gate.
            while (true)
            {
                ThrowIfDisposed();
                long version = Volatile.Read(ref _clockObservationVersion);
                observed = _utcNowProvider();
                Monitor.Enter(_stateGate);
                if (version == _clockObservationVersion) break;
                Monitor.Exit(_stateGate);
            }
        }
        else Monitor.Enter(_stateGate);
        if (_operationDepth++ == 0)
        {
            _operationUtc = observed;
            if (needsObservation) Volatile.Write(ref _clockObservationVersion, _clockObservationVersion + 1);
            if (_pendingEvents is null)
            {
                _pendingEvents = _spareEvents;
                _spareEvents = null;
            }
            if (_pendingCancellations is null)
            {
                _pendingCancellations = _spareCancellations;
                _spareCancellations = null;
            }
        }
        return new(this);
    }

    private void DeferCancellation(Action? cancellation)
    {
        if (cancellation is not null) (_pendingCancellations ??= new List<Action>(2)).Add(cancellation);
    }

    private void ExitOperation()
    {
        List<PendingEvent>? events = null;
        List<Action>? cancellations = null;
        if (--_operationDepth == 0)
        {
            _operationUtc = null;
            if (_pendingEvents is { Count: > 0 })
            {
                events = _pendingEvents;
                _pendingEvents = _spareEvents;
                _spareEvents = null;
            }
            if (_pendingCancellations is { Count: > 0 })
            {
                cancellations = _pendingCancellations;
                _pendingCancellations = _spareCancellations;
                _spareCancellations = null;
            }
        }
        Monitor.Exit(_stateGate);
        if (events is null && cancellations is null) return;
        var context = _batchEvents;
        var capture = context?.Value;
        if (capture is not null) context!.Value = null;
        List<Exception>? failures = null;
        try
        {
            if (cancellations is not null)
                foreach (var cancel in cancellations)
                {
                    try { cancel(); }
                    catch (Exception error) { (failures ??= []).Add(error); }
                }
            if (events is not null)
                foreach (var pending in events)
                    foreach (var subscriber in pending.Handlers)
                    {
                        try { ((Action<PlayableMatchEvent>)subscriber)(pending.Event); }
                        catch { /* One failing subscriber must not suppress later observers or fail the committed command. */ }
                    }
        }
        finally
        {
            if (capture is not null) context!.Value = capture;
            events?.Clear();
            cancellations?.Clear();
            lock (_stateGate)
            {
                if (events is not null) _spareEvents ??= events;
                if (cancellations is not null) _spareCancellations ??= cancellations;
            }
        }
        if (failures is { Count: 1 }) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null) throw new AggregateException(failures);
    }

    private readonly struct OperationScope(UciPlayableMatchSession owner) : IDisposable
    {
        public void Dispose() => owner.ExitOperation();
    }
    private readonly record struct PendingEvent(Delegate[] Handlers, PlayableMatchEvent Event);
    private sealed class BatchCapture
    {
        public readonly List<PlayableMatchEvent> Events = [];
        public bool IsActive = true;
    }

	private void SetForcedResult(PlayableMatchResult result, DateTimeOffset? completedAtUtc = null, string? declaredDrawMove = null)
	{
		_positionRevision++;
		_forcedResult = result;
		_declaredDrawMove = declaredDrawMove;
		_pendingPromotion = null;
		FreezeClock(completedAtUtc);
		_claimableResult = null;
		_drawOfferedBy = null;
		_lastResult = result;
		ResetBackgroundState();
		UpdateCurrentStateMetadata(resultOverride: result);
		RaiseEvent(PlayableMatchEventKind.ResultChanged, state: _hasCurrentState ? _currentState : null, result: result);
	}

	private void UpdateCurrentStateMetadata(PlayableMatchResult? resultOverride = null)
	{
		if (!_hasCurrentState)
			return;

		_currentState = _currentState with
		{
			PendingPromotion = _pendingPromotion,
			Result = resultOverride ?? _currentState.Result,
			ClaimableResult = _claimableResult,
			DrawOfferedBy = _drawOfferedBy,
			Clock = GetClockSnapshot()
		};
	}

	private PlayableMatchMoveData BuildMoveData(
		Fen                previousFen,
		Fen                resultingFen,
		string             move,
		MoveClassification classification)
	{
		char movingSymbol = classification.MovingPiece;
		var movingPiece = BuildPiece(movingSymbol);
		PlayableMatchPiece? capturedPiece = classification.CapturedPiece.HasValue
			? BuildPiece(classification.CapturedPiece.Value)
			: null;
		PlayableMatchPiece? promotionPiece = classification.IsPromotion
			? BuildPiece(previousFen.ActiveColor == 'w' ? char.ToUpperInvariant(move[4]) : move[4])
			: null;
		PlayableMatchSecondaryMove? secondaryMove = BuildSecondaryMove(previousFen.ActiveColor, classification);

		return new(
			_playedMoves.Count,
			previousFen.FullmoveNumber,
			previousFen.ActiveColor,
			move,
			move[..2],
			move.Substring(2, 2),
			movingPiece,
			capturedPiece,
			promotionPiece,
			secondaryMove,
			classification,
			previousFen,
			resultingFen
		);
	}

	private static PlayableMatchPiece BuildPiece(char symbol) =>
		new(char.IsUpper(symbol) ? 'w' : 'b', char.ToLowerInvariant(symbol), symbol);

	private static PlayableMatchSecondaryMove? BuildSecondaryMove(char movingSide, MoveClassification classification)
	{
		if (classification.IsKingsideCastling)
		{
			char rook = movingSide == 'w' ? 'R' : 'r';
			return new(
				movingSide == 'w' ? "h1" : "h8",
				movingSide == 'w' ? "f1" : "f8",
				BuildPiece(rook)
			);
		}

		if (classification.IsQueensideCastling)
		{
			char rook = movingSide == 'w' ? 'R' : 'r';
			return new(
				movingSide == 'w' ? "a1" : "a8",
				movingSide == 'w' ? "d1" : "d8",
				BuildPiece(rook)
			);
		}

		return null;
	}

	private static string SelectFallbackControlledMove(PlayableMatchState state)
	{
		if (state.LegalMoves.IsDefaultOrEmpty || state.LegalMoves.Length == 0)
			throw new InvalidOperationException("No legal moves are available for the current controlled side.");

		string? checkMove = null;
		string? captureMove = null;
		string? promotionMove = null;

		foreach (string legalMove in state.LegalMoves)
		{
			var classification = state.Fen.ClassifyMoveFully(legalMove);
			if (classification.IsMate)
				return legalMove;

			if (checkMove is null && classification.IsCheck)
				checkMove = legalMove;

			if (captureMove is null && classification.IsCapture)
				captureMove = legalMove;

			if (promotionMove is null && classification.IsPromotion)
				promotionMove = legalMove;
		}

		return checkMove ?? captureMove ?? promotionMove ?? state.LegalMoves[0];
	}

	/// <summary>
	///     Plays engine-controlled turns until the match is terminal or the supplied ply limit is reached.
	/// </summary>
	public async Task<PlayableMatchState> PlayUntilTerminalAsync(int maxPlies = int.MaxValue, CancellationToken ct = default)
	{
		ThrowIfDisposed();
		if (maxPlies <= 0)
			throw new ArgumentOutOfRangeException(nameof(maxPlies), "Value must be greater than zero.");

        await RefreshAsync(ct).ConfigureAwait(false);

		for (var i = 0; i < maxPlies; i++)
		{
			var state = CurrentState;
			if (state.Result.IsTerminal)
				return state;

			if (GetController(state.Fen.ActiveColor) != MatchSideControllerKind.Engine)
				throw new InvalidOperationException("PlayUntilTerminalAsync requires engine control for the current side.");

			await PlayControlledMoveAsync(ct).ConfigureAwait(false);
			await RefreshAsync(ct).ConfigureAwait(false);
		}

		return CurrentState;
	}

	/// <summary>Cancels engine turns and optional analysis, then releases idle session resources. Engine clients remain caller-owned.</summary>
	public void Dispose()
    {
        bool ownsShutdown = false;
        try
        {
            using var operation = EnterOperation();
            if (_disposed) return;
            _disposed = true;
            ownsShutdown = true;
            _shutdownCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_activeEngineTurns != 0)
                _engineTurnsStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            DeferCancellation(_positionAnalysis?.BeginCancellation(dispose: true));
            DeferCancellation(_classifications.BeginCancellation());
            if (_engineLifetimeCancellation is not null) DeferCancellation(_engineLifetimeCancellation.Cancel);
        }
        catch (Exception error)
        {
            using (EnterOperation()) _shutdownFailure = ExceptionDispatchInfo.Capture(error);
            throw;
        }
        finally
        {
            if (ownsShutdown)
            {
                using var operation = EnterOperation();
                _engineShutdownSignaled = true;
                ReleaseEngineResourcesWhenIdle();
                _shutdownCompleted!.TrySetResult(true);
            }
        }
    }

    private void ReleaseEngineResourcesWhenIdle()
    {
        if (!_disposed || !_engineShutdownSignaled || _activeEngineTurns != 0 || _engineResourcesDisposed) return;
        _engineResourcesDisposed = true;
        _engineLifetimeCancellation?.Dispose();
        _engineTurnGate?.Dispose();
        _engineTurnsStopped?.TrySetResult(true);
    }

	/// <summary>Cancels and joins engine turns and analysis workers before the caller disposes the engine clients.</summary>
	public async ValueTask DisposeAsync()
    {
        try { Dispose(); }
        catch { /* Preserve the shutdown failure, but observe every worker before reporting it. */ }
        Task shutdown;
        using (EnterOperation()) shutdown = _shutdownCompleted?.Task ?? Task.CompletedTask;
        await shutdown.ConfigureAwait(false);
        Task engineTurns;
        using (EnterOperation()) engineTurns = _engineTurnsStopped?.Task ?? Task.CompletedTask;
        Exception? workerFailure = null;
        try
        {
            await Task.WhenAll(_positionAnalysis?.DisposeAsync().AsTask() ?? Task.CompletedTask,
                _classifications.ActiveWorker ?? Task.CompletedTask, engineTurns).ConfigureAwait(false);
        }
        catch (Exception error) { workerFailure = error; }
        if (_shutdownFailure is { } shutdownFailure)
        {
            if (workerFailure is not null) throw new AggregateException(shutdownFailure.SourceException, workerFailure);
            shutdownFailure.Throw();
        }
        if (workerFailure is not null) ExceptionDispatchInfo.Capture(workerFailure).Throw();
    }

	private void ThrowIfDisposed()
	{
		if (Volatile.Read(ref _disposed)) throw new ObjectDisposedException(nameof(UciPlayableMatchSession));
	}

	private static char Opposite(char color) => color == 'w' ? 'b' : 'w';

	private readonly record struct MatchOutcome(
		PlayableMatchResult  Result,
		PlayableMatchResult? ClaimableResult
	);
}

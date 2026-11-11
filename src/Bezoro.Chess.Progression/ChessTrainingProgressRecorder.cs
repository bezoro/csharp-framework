using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Types;
using Bezoro.PlayerProfiles;

namespace Bezoro.Chess.Progression;

/// <summary>Explicit application intent; lesson and replay sessions never award live training progress.</summary>
public enum ChessTrainingProgressMode
{
    /// <summary>No match recording is enabled.</summary>
    Disabled,
    /// <summary>New live training play may produce one terminal record.</summary>
    Training,
    /// <summary>Imported/replayed history is presentation only.</summary>
    Replay,
    /// <summary>Study owns progression through its separately verified completion receipts.</summary>
    Lesson
}

/// <summary>One immutable rejected record and its admission order within an observer.</summary>
public readonly struct ChessTrainingCaptureRejection
{
    internal ChessTrainingCaptureRejection(ChessTrainingMatch match, long sequence) { Match = match; Sequence = sequence; }
    /// <summary>Gets the complete immutable match to retain and recover after accepted predecessors drain.</summary>
    public ChessTrainingMatch Match { get; }
    /// <summary>Gets the rejection count at admission; use it to order concurrent notifications and acknowledge observed failures.</summary>
    public long Sequence { get; }
}

/// <summary>Opt-in binding from typed UCI events to durable local training progress.</summary>
/// <remarks>
/// Bind before play and set context before loading or starting a match. Historical position loads never award training Elo:
/// their new UCI GameId cannot authenticate the identity of an earlier saved match. New live moves after an empty-history load are eligible.
/// Undo and repeated terminal notifications retain the current GameId and cannot award twice. Changing context during a game invalidates that game.
/// No engine objects or Unity calls are used. An on-demand data worker drains captured immutable records; failed writes remain queued with their original timestamps.
/// Admission is bounded; overflow reports the immutable rejected record through CaptureRejected and retains a diagnostic until explicitly acknowledged.
/// Dispose stops observation; DisposeAsync also drains. Hosts must await shutdown drainage and surface failures instead of abandoning pending writes.
/// </remarks>
public sealed class ChessTrainingProgressRecorder : IDisposable, IAsyncDisposable
{
    private readonly UciGameEngineSession _session;
    private readonly ChessPlayerProgress _progress;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly object _sync = new();
    private readonly Queue<ChessTrainingMatch> _pending = new();
    private Task _draining = Task.CompletedTask;
    private bool _running;
    private bool _disposed;
    private Exception? _lastError;
    private Exception? _captureError;
    private long _rejectedCaptureCount;
    private ChessTrainingCaptureRejection? _lastRejectedCapture;
    private Action<ChessTrainingCaptureRejection>? _captureRejected;
    private Guid _gameId;
    private Guid _invalidatedGameId;
    private bool _hasContext;
    private bool _hasLiveMove;
    private bool _invalidated;
    private bool _captured;
    private ChessTrainingProgressMode _mode;
    private string _opponentId = string.Empty;
    private double _opponentRating;
    private char _playerSide;
    private string? _lastMoveFen;
    private int _lastMovePly;

    /// <summary>Observes one session. The default mode is disabled; timestamps are captured once from terminal clocks or the supplied clock.</summary>
    public ChessTrainingProgressRecorder(UciGameEngineSession session, ChessPlayerProgress progress,
        string opponentId, double opponentRating, char playerSide,
        ChessTrainingProgressMode mode = ChessTrainingProgressMode.Disabled, Func<DateTimeOffset>? utcNowProvider = null,
        int pendingCapacity = 256)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _progress = progress ?? throw new ArgumentNullException(nameof(progress));
        if (pendingCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(pendingCapacity));
        PendingCapacity = pendingCapacity;
        _utcNow = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
        SetContext(mode, opponentId, opponentRating, playerSide);
        _session.GameStarted += OnStarted;
        _session.PositionLoaded += OnLoaded;
        _session.MoveMade += OnMove;
        _session.MoveUndone += OnUndo;
        _session.GameFinished += OnFinished;
    }

    /// <summary>Gets the last captured observation/storage failure, or null. DrainAsync also reports it.</summary>
    public Exception? LastError { get { lock (_sync) return _captureError ?? _lastError; } }
    /// <summary>Gets the number of terminal records awaiting a successful durable write.</summary>
    public int PendingCount { get { lock (_sync) return _pending.Count; } }
    /// <summary>Gets the maximum accepted records awaiting storage, including an in-flight write.</summary>
    public int PendingCapacity { get; }
    /// <summary>Gets the cumulative rejected-admission count, saturating at Int64.MaxValue.</summary>
    public long RejectedCaptureCount { get { lock (_sync) return _rejectedCaptureCount; } }
    /// <summary>Gets the most recent immutable rejected record. This bounded diagnostic does not retain earlier rejections.</summary>
    public ChessTrainingCaptureRejection? LastRejectedCapture { get { lock (_sync) return _lastRejectedCapture; } }
    /// <summary>Reports each rejected record on the terminal-event delivery thread, outside the recorder gate.</summary>
    /// <remarks>The host must retain each payload and pause new play or recover it in Sequence order after accepted predecessors drain. Notifications may execute concurrently. Every subscriber is attempted; failures surface through LastError after admission failure is acknowledged.</remarks>
    public event Action<ChessTrainingCaptureRejection>? CaptureRejected
    {
        add
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ChessTrainingProgressRecorder));
                _captureRejected += value;
            }
        }
        remove { lock (_sync) _captureRejected -= value; }
    }

    /// <summary>Acknowledges that the host observed and retained rejected records through the supplied count.</summary>
    /// <remarks>Does not retry writes or award rejected matches. Returns false if a newer rejection arrived or there is no admission failure to acknowledge.</remarks>
    public bool AcknowledgeRejectedCaptures(long observedCount)
    {
        if (observedCount < 0) throw new ArgumentOutOfRangeException(nameof(observedCount));
        lock (_sync)
        {
            if (_captureError is null || observedCount != _rejectedCaptureCount) return false;
            _captureError = null;
            return true;
        }
    }

    /// <summary>Sets explicit match intent and actual configured opponent strength/perspective before a new session position or game.</summary>
    /// <remarks>A changed context invalidates the session's current logical game even when its events are still queued. Reapplying identical metadata is harmless.</remarks>
    public void SetContext(ChessTrainingProgressMode mode, string opponentId, double opponentRating, char playerSide)
    {
        if (mode < ChessTrainingProgressMode.Disabled || mode > ChessTrainingProgressMode.Lesson) throw new ArgumentOutOfRangeException(nameof(mode));
        ProgressText.Require(opponentId, nameof(opponentId));
        _ = new EloRating(opponentRating);
        if (playerSide is not ('w' or 'b')) throw new ArgumentOutOfRangeException(nameof(playerSide));
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ChessTrainingProgressRecorder));
            if (_mode == mode && _opponentId == opponentId && _opponentRating.Equals(opponentRating) && _playerSide == playerSide) return;
            if (_hasContext)
            {
                _invalidatedGameId = _session.CurrentGameId;
                if (_gameId == _invalidatedGameId) _invalidated = true;
            }
            _mode = mode; _opponentId = opponentId; _opponentRating = opponentRating; _playerSide = playerSide;
            _hasContext = true; _hasLiveMove = false;
        }
    }

    private void Track(Guid gameId)
    {
        if (gameId == Guid.Empty || gameId == _gameId) return;
        _gameId = gameId; _hasLiveMove = _captured = false;
        _invalidated = gameId == _invalidatedGameId;
        _lastMoveFen = null; _lastMovePly = 0;
    }
    private void OnStarted(GameStartedEvent started)
    {
        lock (_sync) { if (!_disposed && started.GameId == _session.CurrentGameId) Track(started.GameId); }
    }
    private void OnLoaded(PositionLoadedEvent loaded)
    {
        lock (_sync)
        {
            if (_disposed || loaded.GameId != _session.CurrentGameId) return;
            Track(loaded.GameId);
            _hasLiveMove = false;
            if (!loaded.PlayedMoves.IsDefaultOrEmpty) _invalidated = true;
        }
    }
    private void OnMove(GameMoveEvent move)
    {
        lock (_sync)
        {
            if (_disposed || move.GameId != _session.CurrentGameId) return;
            Track(move.GameId);
            if (_gameId == Guid.Empty) return;
            _hasLiveMove = true;
            _lastMoveFen = move.ResultingFen.Raw;
            _lastMovePly = move.Ply;
        }
    }
    private void OnUndo(MoveUndoneEvent undone)
    {
        lock (_sync)
        {
            if (_disposed || undone.GameId != _session.CurrentGameId || undone.GameId != _gameId
                || !_hasLiveMove || undone.Moves.IsDefaultOrEmpty) return;
            _lastMoveFen = undone.ResultingFen.Raw;
            _lastMovePly = undone.Moves[0].Ply - 1;
            _hasLiveMove = _lastMovePly > 0;
        }
    }
    private void OnFinished(GameFinishedEvent finished)
    {
        ChessTrainingCaptureRejection? rejected = null;
        Action<ChessTrainingCaptureRejection>? rejectionHandlers = null;
        try
        {
            lock (_sync) { if (!CanCapture(finished)) return; }
            // The host clock may run arbitrary code, including context changes or disposal.
            // Keep it outside our gate and revalidate eligibility before publishing a record.
            DateTimeOffset completedAt = finished.State.Clock?.SnapshotUtc ?? _utcNow();
            lock (_sync)
            {
                if (!CanCapture(finished)) return;
                var match = new ChessTrainingMatch(_gameId.ToString("N"), _opponentId, _opponentRating,
                    _playerSide, finished.State.Result, completedAt, ChessMatchProgressSource.LiveTraining);
                _captured = true;
                if (_pending.Count == PendingCapacity)
                {
                    if (_rejectedCaptureCount != long.MaxValue) _rejectedCaptureCount++;
                    _lastRejectedCapture = rejected = new ChessTrainingCaptureRejection(match, _rejectedCaptureCount);
                    _captureError = new InvalidOperationException("The pending training progress capacity was reached. The host must retain and recover rejected match " + match.MatchId + ".");
                    rejectionHandlers = _captureRejected;
                }
                else
                {
                    _pending.Enqueue(match);
                    if (!_running && _lastError is null) StartWorker();
                }
            }
        }
        catch (Exception exception) { lock (_sync) _lastError = exception; }
        if (rejected is { } capture && rejectionHandlers is not null)
            foreach (Action<ChessTrainingCaptureRejection> handler in rejectionHandlers.GetInvocationList())
            {
                try { handler(capture); }
                catch (Exception exception) { lock (_sync) _lastError = exception; }
            }
    }

    private bool CanCapture(GameFinishedEvent finished)
    {
        if (_disposed || _mode != ChessTrainingProgressMode.Training || _gameId == Guid.Empty
            || finished.GameId != _gameId || finished.GameId != _session.CurrentGameId
            || !_hasLiveMove || _invalidated || _captured || !finished.State.Result.IsTerminal) return false;
        if (_progress.HasRecordedMatch(_gameId.ToString("N"))) { _captured = true; return false; }
        // Queued delivery must still describe the current result and last observed live position.
        var state = finished.State;
        var current = _session.State;
        return state.Result == current.Result && state.CurrentFen.Raw == current.CurrentFen.Raw
            && state.PlayedMoves.SequenceEqual(current.PlayedMoves)
            && state.CurrentFen.Raw == _lastMoveFen && state.PlayedMoves.Count == _lastMovePly;
    }

    private void StartWorker()
    {
        _running = true;
        try { _draining = Task.Run(ProcessQueueAsync); }
        catch { _running = false; throw; }
    }
    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            ChessTrainingMatch next;
            lock (_sync)
            {
                if (_pending.Count == 0) { _running = false; return; }
                next = _pending.Peek();
            }
            try { await _progress.RecordMatchAsync(next).ConfigureAwait(false); }
            catch (Exception exception)
            {
                lock (_sync) { _lastError = exception; _running = false; }
                return;
            }
            lock (_sync) _pending.Dequeue();
        }
    }

    /// <summary>Waits for captured writes and reports any failure. Stop observing first when using it as a shutdown barrier.</summary>
    /// <remarks>This does not deliver callbacks still queued in the session's synchronization context. Let terminal events arrive before disposing when their results must be retained.</remarks>
    public async Task DrainAsync()
    {
        while (true)
        {
            Task pending;
            lock (_sync) pending = _draining;
            await pending.ConfigureAwait(false);
            lock (_sync)
            {
                if ((_captureError ?? _lastError) is { } error)
                    throw new InvalidOperationException("Local training progress could not be recorded. Pending records retain their original identities and timestamps; rejected records require explicit host recovery.", error);
                if (!_running && _pending.Count == 0) return;
            }
        }
    }
    /// <summary>Retries retained failed writes without changing their match metadata, then drains. This also works after observation was disposed.</summary>
    public Task RetryPendingAsync()
    {
        lock (_sync)
        {
            if (!_running)
            {
                _lastError = null;
                if (_pending.Count != 0) StartWorker();
            }
        }
        return DrainAsync();
    }
    /// <summary>Stops new event delivery. Already captured records continue draining and must still be awaited.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _captureRejected = null;
            _session.GameStarted -= OnStarted;
            _session.PositionLoaded -= OnLoaded;
            _session.MoveMade -= OnMove;
            _session.MoveUndone -= OnUndo;
            _session.GameFinished -= OnFinished;
        }
    }
    /// <summary>Stops observation and waits for all queued records, surfacing any storage failure.</summary>
    public async ValueTask DisposeAsync() { Dispose(); await DrainAsync().ConfigureAwait(false); }
}

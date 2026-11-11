using Bezoro.Chess.Progression;
using Bezoro.Chess.Study;
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Types;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.Persistence;
using Bezoro.PlayerProfiles;
using Xunit;

namespace Bezoro.Chess.Progression.Tests;

public sealed class TrainingRecorderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AClockCallbackCannotCaptureAfterItDisposesOrInvalidatesTheObserver(bool dispose)
    {
        using var fixture = new RecorderFixture();
        var progress = await fixture.Open();
        using var session = Session();
        ChessTrainingProgressRecorder? recorder = null;
        recorder = new ChessTrainingProgressRecorder(session, progress, "opponent", 1200, 'w', ChessTrainingProgressMode.Training, () =>
        {
            if (dispose) recorder!.Dispose();
            else recorder!.SetContext(ChessTrainingProgressMode.Lesson, "opponent", 1200, 'w');
            return DateTimeOffset.UnixEpoch;
        });
        await using (recorder)
        {
            await session.NewGameAsync();
            await FoolsMate(session);
            await recorder.DrainAsync();
            Assert.Empty(progress.Snapshot.History);
            Assert.Equal(0, recorder.PendingCount);
            Assert.Null(recorder.LastError);
        }
    }

    [Fact]
    public async Task AClockCallbackCanReadRecorderStateOnAnotherThread()
    {
        using var fixture = new RecorderFixture();
        var progress = await fixture.Open();
        using var session = Session();
        ChessTrainingProgressRecorder? recorder = null;
        recorder = new ChessTrainingProgressRecorder(session, progress, "opponent", 1200, 'w', ChessTrainingProgressMode.Training, () =>
        {
            var read = Task.Factory.StartNew(() => recorder!.PendingCount,
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (!read.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The clock callback held the recorder gate.");
            Assert.Equal(0, read.Result);
            return DateTimeOffset.UnixEpoch;
        });
        await using (recorder)
        {
            await session.NewGameAsync();
            await FoolsMate(session);
            await recorder.DrainAsync();
            Assert.Single(progress.Snapshot.History);
            Assert.Null(recorder.LastError);
        }
    }

    [Fact]
    public async Task PendingCapacityRejectsObservablyAndSuccessfulRetryCannotEraseTheAdmissionFailure()
    {
        using var fixture = new RecorderFixture();
        var progress = await fixture.Open();
        using var session = Session();
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChessTrainingProgressRecorder(session, progress,
            "opponent", 1200, 'w', pendingCapacity: 0));
        var recorder = new ChessTrainingProgressRecorder(session, progress, "opponent", 1200, 'w',
            ChessTrainingProgressMode.Training, () => DateTimeOffset.UnixEpoch, pendingCapacity: 1);
        var rejected = new List<ChessTrainingCaptureRejection>();
        recorder.CaptureRejected += match =>
        {
            var inspect = Task.Factory.StartNew(() => recorder.PendingCount,
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (!inspect.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Rejection notification held the recorder gate.");
            Assert.Equal(1, inspect.Result);
            rejected.Add(match);
        };
        fixture.Store.FailWrites = true;
        await session.NewGameAsync();
        await FoolsMate(session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.DrainAsync());
        for (int index = 0; index < 2; index++)
        {
            await session.NewGameAsync();
            await FoolsMate(session);
            Assert.Equal(1, recorder.PendingCount);
        }
        Assert.Equal(1, recorder.PendingCapacity);
        Assert.Equal(2, recorder.RejectedCaptureCount);
        Assert.Equal(2, rejected.Count);
        Assert.NotEqual(rejected[0].Match.MatchId, rejected[1].Match.MatchId);
        Assert.Equal(1, rejected[0].Sequence);
        Assert.Equal(2, rejected[1].Sequence);
        Assert.Equal(rejected[1], recorder.LastRejectedCapture);
        Assert.False(recorder.AcknowledgeRejectedCaptures(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => recorder.AcknowledgeRejectedCaptures(-1));
        recorder.Dispose();
        Action<ChessTrainingCaptureRejection> noop = static _ => { };
        Assert.Throws<ObjectDisposedException>(() => recorder.CaptureRejected += noop);
        recorder.CaptureRejected -= noop;
        fixture.Store.FailWrites = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.RetryPendingAsync());
        Assert.Equal(0, recorder.PendingCount);
        Assert.Single(progress.Snapshot.History);
        Assert.NotNull(recorder.LastError);
        Assert.Equal(2, recorder.RejectedCaptureCount);
        Assert.Equal(rejected[1], recorder.LastRejectedCapture);
        Assert.True(recorder.AcknowledgeRejectedCaptures(2));
        Assert.False(recorder.AcknowledgeRejectedCaptures(2));
        foreach (var rejection in rejected.OrderBy(rejection => rejection.Sequence)) Assert.True(await progress.RecordMatchAsync(rejection.Match));
        await recorder.DisposeAsync();
        Assert.Null(recorder.LastError);
        Assert.Equal(3, progress.Snapshot.History.Count);
        Assert.All(progress.Snapshot.History, record => Assert.Equal(DateTimeOffset.UnixEpoch, record.CompletedAt));
    }

    [Fact]
    public async Task PendingCapacityIncludesTheWriteCurrentlyInFlight()
    {
        using var fixture = new RecorderFixture();
        var progress = await fixture.Open();
        using var session = Session();
        fixture.Store.WriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.WriteRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new ChessTrainingProgressRecorder(session, progress, "opponent", 1200, 'w',
            ChessTrainingProgressMode.Training, pendingCapacity: 1);
        try
        {
            await session.NewGameAsync();
            await FoolsMate(session);
            await fixture.Store.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(progress.Snapshot.History);
            await session.NewGameAsync();
            await FoolsMate(session);
            Assert.Equal(1, recorder.PendingCount);
            Assert.Equal(1, recorder.RejectedCaptureCount);
            Assert.NotNull(recorder.LastRejectedCapture);
        }
        finally
        {
            recorder.Dispose();
            fixture.Store.WriteRelease.TrySetResult();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.DrainAsync());
        Assert.Single(progress.Snapshot.History);
        Assert.True(recorder.AcknowledgeRejectedCaptures(1));
        Assert.True(await progress.RecordMatchAsync(recorder.LastRejectedCapture!.Value.Match));
        await recorder.DisposeAsync();
    }

    [Fact]
    public async Task ARejectingSubscriberFailureCannotHideTheRecordFromOtherSubscribers()
    {
        using var fixture = new RecorderFixture();
        var progress = await fixture.Open();
        using var session = Session();
        var recorder = new ChessTrainingProgressRecorder(session, progress, "opponent", 1200, 'w',
            ChessTrainingProgressMode.Training, pendingCapacity: 1);
        fixture.Store.FailWrites = true;
        await session.NewGameAsync();
        await FoolsMate(session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.DrainAsync());
        ChessTrainingCaptureRejection? received = null;
        recorder.CaptureRejected += _ => throw new InvalidOperationException("Subscriber failure.");
        recorder.CaptureRejected += match => received = match;
        await session.NewGameAsync();
        await FoolsMate(session);
        Assert.NotNull(received);
        Assert.Equal(received, recorder.LastRejectedCapture);
        Assert.True(recorder.AcknowledgeRejectedCaptures(recorder.RejectedCaptureCount));
        Assert.Equal("Subscriber failure.", recorder.LastError!.Message);
        recorder.Dispose();
        fixture.Store.FailWrites = false;
        await recorder.RetryPendingAsync();
        Assert.True(await progress.RecordMatchAsync(received.Value.Match));
        await recorder.DisposeAsync();
    }

    [Fact]
    public async Task RealLocalSessionRecordsDerivedResultAndActualOpponentRatingOnce()
    {
        using var fixture = new RecorderFixture(); var progress = await fixture.Open();
        using var session = Session();
        await using var recorder = new ChessTrainingProgressRecorder(session, progress, "training-white", 1400, 'b', ChessTrainingProgressMode.Training,
            () => DateTimeOffset.UnixEpoch);
        await session.NewGameAsync();
        await FoolsMate(session);
        await recorder.DrainAsync();
        var record = Assert.Single(progress.Snapshot.History);
        Assert.Equal(PlayerMatchOutcome.Win, record.Outcome); Assert.Equal("training-white", record.OpponentId);
        Assert.Equal(DateTimeOffset.UnixEpoch, record.CompletedAt);
        Assert.Equal(EloRatings.ApplyResult(new EloRating(1200), new EloRating(1400), PlayerMatchOutcome.Win).FirstPlayer.Value, progress.Snapshot.TrainingRating.Value);
        Assert.Null(recorder.LastError); Assert.Equal(0, recorder.PendingCount);
    }

    [Theory]
    [InlineData(ChessTrainingProgressMode.Disabled)]
    [InlineData(ChessTrainingProgressMode.Replay)]
    [InlineData(ChessTrainingProgressMode.Lesson)]
    public async Task NonTrainingIntentNeverAwardsTerminalSessionProgress(ChessTrainingProgressMode mode)
    {
        using var fixture = new RecorderFixture(); var progress = await fixture.Open(); using var session = Session();
        await using var recorder = new ChessTrainingProgressRecorder(session, progress, "opponent", 1200, 'w', mode);
        await session.NewGameAsync(); await FoolsMate(session); await recorder.DrainAsync();
        Assert.Empty(progress.Snapshot.History); Assert.Equal(0, progress.Snapshot.TrainingRating.GamesPlayed);
        Assert.Null(recorder.LastError);
    }

    [Fact]
    public async Task LoadingTerminalOrHistoricalPositionsCannotTurnReplaysIntoNewTrainingGames()
    {
        using var fixture = new RecorderFixture(); var progress = await fixture.Open(); using var session = Session();
        await using var recorder = new ChessTrainingProgressRecorder(session, progress, "opponent", 1200, 'w', ChessTrainingProgressMode.Training);
        await session.LoadMatchAsync(new PlayableMatchSetup(Fen.Default, new[] { "f2f3", "e7e5", "g2g4", "d8h4" }));
        await recorder.DrainAsync(); Assert.Empty(progress.Snapshot.History);
        await session.UndoAsync(); await session.MakeMoveAsync("d8h4"); await recorder.DrainAsync();
        Assert.Empty(progress.Snapshot.History);
        await session.LoadMatchAsync(new PlayableMatchSetup(Fen.Default, new[] { "f2f3", "e7e5", "g2g4" }));
        await session.MakeMoveAsync("d8h4"); await recorder.DrainAsync(); Assert.Empty(progress.Snapshot.History);
        await session.NewGameAsync(); await FoolsMate(session); await recorder.DrainAsync();
        Assert.Single(progress.Snapshot.History);
    }

    [Fact]
    public async Task UndoAndReplayedTerminalIncludingRecorderRebindDoNotFarmOrConflict()
    {
        using var fixture = new RecorderFixture(); var progress = await fixture.Open(); using var session = Session();
        var recorder = new ChessTrainingProgressRecorder(session, progress, "opponent", 1200, 'w', ChessTrainingProgressMode.Training);
        await session.NewGameAsync(); await FoolsMate(session); await recorder.DrainAsync();
        await session.UndoAsync(); await session.MakeMoveAsync("d8h4"); await recorder.DrainAsync();
        Assert.Single(progress.Snapshot.History); Assert.Null(recorder.LastError);
        await recorder.DisposeAsync();
        await using var rebound = new ChessTrainingProgressRecorder(session, progress, "opponent", 1200, 'w', ChessTrainingProgressMode.Training);
        await session.UndoAsync(); await session.MakeMoveAsync("d8h4"); await rebound.DrainAsync();
        Assert.Single(progress.Snapshot.History); Assert.Null(rebound.LastError);
    }

    [Fact]
    public async Task ChangedContextInvalidatesCurrentGameUntilNewIdentityAndBlocksLessonMate()
    {
        using var fixture = new RecorderFixture(); var progress = await fixture.Open(); using var session = Session();
        await using var recorder = new ChessTrainingProgressRecorder(session, progress, "opponent", 1200, 'w', ChessTrainingProgressMode.Training);
        await session.NewGameAsync(); await session.MakeMoveAsync("f2f3");
        recorder.SetContext(ChessTrainingProgressMode.Lesson, "opponent", 1200, 'w');
        await session.MakeMoveAsync("e7e5"); await session.MakeMoveAsync("g2g4"); await session.MakeMoveAsync("d8h4");
        await recorder.DrainAsync(); Assert.Empty(progress.Snapshot.History);
        recorder.SetContext(ChessTrainingProgressMode.Training, "opponent", 1500, 'b');
        await session.UndoAsync(); await session.MakeMoveAsync("d8h4"); await recorder.DrainAsync();
        Assert.Empty(progress.Snapshot.History);
        await session.NewGameAsync(); await FoolsMate(session); await recorder.DrainAsync();
        Assert.Equal(PlayerMatchOutcome.Win, Assert.Single(progress.Snapshot.History).Outcome);
    }

    [Fact]
    public async Task QueuedLessonEventsCannotBecomeTrainingAfterContextChangesBeforeDelivery()
    {
        using var fixture = new RecorderFixture(); var progress = await fixture.Open();
        var context = new QueuedContext(); using var session = Session(context);
        await using var recorder = new ChessTrainingProgressRecorder(session, progress, "lesson", 1200, 'w', ChessTrainingProgressMode.Lesson);
        await session.NewGameAsync(); await FoolsMate(session);
        recorder.SetContext(ChessTrainingProgressMode.Training, "actual-training", 1500, 'b');
        context.Drain(); await recorder.DrainAsync();
        Assert.Empty(progress.Snapshot.History); Assert.Null(recorder.LastError);
        await session.NewGameAsync(); var trainingId = session.CurrentGameId;
        await FoolsMate(session); context.Drain(); await recorder.DrainAsync();
        var recorded = Assert.Single(progress.Snapshot.History);
        Assert.Equal(trainingId.ToString("N"), recorded.MatchId);
        Assert.Equal("actual-training", recorded.OpponentId);
        Assert.Equal(EloRatings.ApplyResult(new EloRating(1200), new EloRating(1500), PlayerMatchOutcome.Win).FirstPlayer.Value,
            progress.Snapshot.TrainingRating.Value);
    }

    [Fact]
    public async Task LateOldTerminalCannotMatchAnIdenticalCurrentTerminalBeforeItsOwnEventArrives()
    {
        using var fixture = new RecorderFixture(); var progress = await fixture.Open();
        var context = new QueuedContext(); using var session = Session(context);
        await using var recorder = new ChessTrainingProgressRecorder(session, progress, "old", 1200, 'w', ChessTrainingProgressMode.Lesson);
        await session.NewGameAsync(); await FoolsMate(session);
        var previousCallbacks = context.TakeAll(); Assert.NotEmpty(previousCallbacks);
        recorder.SetContext(ChessTrainingProgressMode.Training, "new", 1450, 'b');
        await session.NewGameAsync(); var trainingId = session.CurrentGameId;
        await FoolsMate(session);
        var currentCallbacks = context.TakeAll(); Assert.NotEmpty(currentCallbacks);
        // Hold back the current game's terminal callback while delivering all of its live move events.
        foreach (var callback in currentCallbacks.Take(currentCallbacks.Count - 1)) callback();
        foreach (var callback in previousCallbacks) callback();
        await recorder.DrainAsync(); Assert.Empty(progress.Snapshot.History);
        currentCallbacks[^1](); await recorder.DrainAsync();
        var recorded = Assert.Single(progress.Snapshot.History);
        Assert.Equal(trainingId.ToString("N"), recorded.MatchId); Assert.Equal("new", recorded.OpponentId);
        Assert.Null(recorder.LastError);
    }

    [Fact]
    public async Task DrainingCapturedWritesDoesNotPumpOrPromiseUndeliveredSynchronizationContextEvents()
    {
        using var fixture = new RecorderFixture(); var progress = await fixture.Open();
        var context = new QueuedContext(); using var session = Session(context);
        var recorder = new ChessTrainingProgressRecorder(session, progress, "opponent", 1200, 'w', ChessTrainingProgressMode.Training);
        await session.NewGameAsync(); await FoolsMate(session); await recorder.DrainAsync();
        Assert.Empty(progress.Snapshot.History);
        await recorder.DisposeAsync(); context.Drain();
        Assert.Empty(progress.Snapshot.History); Assert.Null(recorder.LastError);
    }

    [Fact]
    public async Task ResignationAfterUndoUsesTheRetainedLivePositionAndUndoToZeroCannotAward()
    {
        using var fixture = new RecorderFixture(); var progress = await fixture.Open(); using var session = Session();
        await using var recorder = new ChessTrainingProgressRecorder(session, progress, "opponent", 1200, 'w', ChessTrainingProgressMode.Training);
        await session.NewGameAsync();
        foreach (string move in new[] { "e2e4", "e7e5", "g1f3" }) await session.MakeMoveAsync(move);
        await session.UndoAsync(); await session.ResignAsync('w'); await recorder.DrainAsync();
        Assert.Equal(PlayerMatchOutcome.Loss, Assert.Single(progress.Snapshot.History).Outcome);
        await session.NewGameAsync(); await session.MakeMoveAsync("e2e4"); await session.UndoAsync();
        await session.ResignAsync('w'); await recorder.DrainAsync();
        Assert.Single(progress.Snapshot.History);
    }

    [Fact]
    public async Task FailureIsSurfacedAndRetriedWithTheOriginalTerminalIdentityAndTimestampAfterDispose()
    {
        using var fixture = new RecorderFixture(); var progress = await fixture.Open(); using var session = Session();
        var now = DateTimeOffset.UnixEpoch;
        var recorder = new ChessTrainingProgressRecorder(session, progress, "opponent", 1200, 'w', ChessTrainingProgressMode.Training, () => now);
        fixture.Store.FailWrites = true;
        await session.NewGameAsync(); await FoolsMate(session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.DrainAsync());
        Assert.IsType<IOException>(recorder.LastError); Assert.Equal(1, recorder.PendingCount); Assert.Empty(progress.Snapshot.History);
        now = now.AddDays(1); recorder.Dispose(); fixture.Store.FailWrites = false;
        await recorder.RetryPendingAsync();
        Assert.Equal(DateTimeOffset.UnixEpoch, Assert.Single(progress.Snapshot.History).CompletedAt);
        Assert.Null(recorder.LastError); Assert.Equal(0, recorder.PendingCount);
        await recorder.DisposeAsync();
    }

    [Fact]
    public async Task DisposedObserverDoesNotCaptureFutureGamesAndUnchangedContextDoesNotInvalidatePlay()
    {
        using var fixture = new RecorderFixture(); var progress = await fixture.Open(); using var session = Session();
        var recorder = new ChessTrainingProgressRecorder(session, progress, "opponent", 1200, 'w', ChessTrainingProgressMode.Training);
        await session.NewGameAsync(); await session.MakeMoveAsync("f2f3");
        recorder.SetContext(ChessTrainingProgressMode.Training, "opponent", 1200, 'w');
        await session.MakeMoveAsync("e7e5"); await session.MakeMoveAsync("g2g4"); await session.MakeMoveAsync("d8h4");
        await recorder.DisposeAsync(); Assert.Single(progress.Snapshot.History);
        await session.NewGameAsync(); await FoolsMate(session);
        await recorder.DrainAsync(); Assert.Single(progress.Snapshot.History);
        Assert.Throws<ObjectDisposedException>(() => recorder.SetContext(ChessTrainingProgressMode.Training, "opponent", 1200, 'w'));
    }

    private static UciGameEngineSession Session(SynchronizationContext? context = null) => new(syncContext: context,
        options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = false });
    private static async Task FoolsMate(UciGameEngineSession session)
    {
        foreach (string move in new[] { "f2f3", "e7e5", "g2g4", "d8h4" }) await session.MakeMoveAsync(move);
        Assert.Equal(PlayableMatchResultReason.Checkmate, session.State.Result.Reason);
    }
    private sealed class RecorderFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "bezoro-chess-recorder-" + Guid.NewGuid().ToString("N"));
        internal RecorderFixture() => Store = new FailingStore(new AtomicFileSaveStore(_directory, 1024 * 1024, false));
        internal FailingStore Store { get; }
        internal ValueTask<ChessPlayerProgress> Open()
        {
            var lesson = new GuidedLessonDefinition("intro", "Introduction", PlayableMatchSetup.Standard,
                new[] { new GuidedLessonStep("Read the board.") });
            var catalog = ChessProgressCatalog.CreateStarter(new ChessLearningCampaign("test", new[] { new ChessLearningStage(new(1), lesson) }));
            return ChessPlayerProgress.OpenAsync(Store, "profile", new PlayerProfile("local-player", "Player"), catalog);
        }
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
    private sealed class FailingStore(ISaveStore inner) : ISaveStore
    {
        internal bool FailWrites;
        internal TaskCompletionSource? WriteStarted;
        internal TaskCompletionSource? WriteRelease;
        public async ValueTask WriteAsync(string slot, uint schemaVersion, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            if (FailWrites) throw new IOException("Injected recording failure.");
            WriteStarted?.TrySetResult();
            if (WriteRelease is { } release) await release.Task.WaitAsync(cancellationToken);
            await inner.WriteAsync(slot, schemaVersion, payload, cancellationToken);
        }
        public ValueTask<SaveReadResult> ReadAsync(string slot, Memory<byte> destination, uint? expectedSchemaVersion = null, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(slot, destination, expectedSchemaVersion, cancellationToken);
        public ValueTask DeleteAsync(string slot, CancellationToken cancellationToken = default) => inner.DeleteAsync(slot, cancellationToken);
    }
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<Action> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_callbacks) _callbacks.Enqueue(() => callback(state));
        }
        internal List<Action> TakeAll()
        {
            lock (_callbacks) { var result = _callbacks.ToList(); _callbacks.Clear(); return result; }
        }
        internal void Drain() { foreach (var callback in TakeAll()) callback(); }
    }
}

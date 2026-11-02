using System.Collections.Concurrent;
using System.Reflection;
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Types;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.UCI.Tests.API;

public sealed class UciGameEngineSessionQueuedAnalysisTests
{
    [Theory]
    [InlineData("classification", "move")]
    [InlineData("completion", "move")]
    [InlineData("evaluation", "move")]
    [InlineData("bestmove", "move")]
    [InlineData("searching", "move")]
    [InlineData("classification", "undo")]
    [InlineData("completion", "undo")]
    [InlineData("evaluation", "undo")]
    [InlineData("bestmove", "undo")]
    [InlineData("searching", "undo")]
    [InlineData("classification", "terminal")]
    [InlineData("completion", "terminal")]
    [InlineData("evaluation", "terminal")]
    [InlineData("bestmove", "terminal")]
    [InlineData("searching", "terminal")]
    public async Task QueuedWorkerNotifications_AreDiscardedAfterGenerationInvalidation(string notification, string change)
    {
        var context = new BlockingQueuedContext();
        await using var session = new UciGameEngineSession(syncContext: context);
        await session.LoadMatchAsync(PlayableMatchSetup.Standard);
        if (change == "undo") await session.MakeMoveAsync("g1f3");
        Fen initial = session.CurrentFen;
        var snapshots = new List<UciState>();
        var published = new List<UciGameEngineSessionEvent>();
        int workerCallbacks = 0;
        session.StateChanged += snapshots.Add;
        session.EventPublished += published.Add;
        session.MoveClassified += _ => workerCallbacks++;
        session.ClassificationCompleted += _ => workerCallbacks++;
        session.EvaluationChanged += _ => workerCallbacks++;
        session.BestMoveChanged += _ => workerCallbacks++;
        session.EngineThinkingStarted += _ => workerCallbacks++;
        context.Drain();
        snapshots.Clear();
        published.Clear();

        await using var stream = session.StreamStateAsync().GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(initial, stream.Current.CurrentFen);
        context.Arm();
        Task worker = Task.Run(() => Publish(session, notification));
        try
        {
            Assert.True(context.Entered.Wait(TimeSpan.FromSeconds(5)), "The worker must reach its queued notification.");
            if (change == "move") await session.MakeMoveAsync("e2e4");
            else if (change == "undo") await session.UndoAsync();
            else await session.ResignAsync();
        }
        finally { context.Release.Set(); }
        await worker;
        context.Drain();

        Assert.NotEmpty(snapshots);
        Assert.Equal(session.CurrentFen, snapshots[^1].CurrentFen);
        Assert.Equal(session.State.Result, snapshots[^1].Result);
        Assert.Equal(0, workerCallbacks);
        Assert.DoesNotContain(published, value => value.Kind is UciGameEngineSessionEventKind.MoveClassified
            or UciGameEngineSessionEventKind.ClassificationCompleted or UciGameEngineSessionEventKind.EvaluationChanged
            or UciGameEngineSessionEventKind.BestMoveChanged or UciGameEngineSessionEventKind.EngineThinkingStarted);
        Assert.Equal(session.CurrentFen, published.Last(value => value.State.HasValue).State!.Value.CurrentFen);
        Assert.Equal(session.State.Result, published.Last(value => value.State.HasValue).State!.Value.Result);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(session.CurrentFen, stream.Current.CurrentFen);
        Assert.Equal(session.State.Result, stream.Current.Result);
    }

    [Theory]
    [InlineData("classification")]
    [InlineData("completion")]
    [InlineData("evaluation")]
    [InlineData("bestmove")]
    public async Task ValidWorkerNotifications_AreDeliveredWhileTheirGenerationIsCurrent(string notification)
    {
        var context = new BlockingQueuedContext();
        await using var session = new UciGameEngineSession(syncContext: context);
        await session.LoadMatchAsync(PlayableMatchSetup.Standard);
        int callbacks = 0;
        session.MoveClassified += _ => callbacks++;
        session.ClassificationCompleted += _ => callbacks++;
        session.EvaluationChanged += _ => callbacks++;
        session.BestMoveChanged += _ => callbacks++;
        context.Drain();
        Publish(session, notification);
        Assert.Equal(0, callbacks);
        context.Drain();
        Assert.Equal(1, callbacks);
    }

    [Theory]
    [InlineData("classification", "pause")]
    [InlineData("completion", "pause")]
    [InlineData("evaluation", "pause")]
    [InlineData("bestmove", "pause")]
    [InlineData("searching", "pause")]
    [InlineData("classification", "resume")]
    [InlineData("completion", "resume")]
    [InlineData("evaluation", "resume")]
    [InlineData("bestmove", "resume")]
    [InlineData("searching", "resume")]
    [InlineData("classification", "offer")]
    [InlineData("completion", "offer")]
    [InlineData("evaluation", "offer")]
    [InlineData("bestmove", "offer")]
    [InlineData("searching", "offer")]
    public async Task QueuedWorkerState_DoesNotOverwriteNewerMetadataInTheSameGeneration(string notification, string change)
    {
        var context = new BlockingQueuedContext();
        var now = DateTimeOffset.UnixEpoch;
        var options = new UciCoordinatorOptions
        {
            TimeControl = new PlayableMatchTimeControl(TimeSpan.FromMinutes(5), TimeSpan.Zero)
        };
        await using var session = new UciGameEngineSession(syncContext: context, options: options, utcNowProvider: () => now);
        await session.LoadMatchAsync(PlayableMatchSetup.Standard);
        if (change == "resume") await session.PauseClockAsync();
        var snapshots = new List<UciState>();
        var canonicalSnapshots = new List<UciState>();
        session.StateChanged += snapshots.Add;
        session.EventPublished += value =>
        {
            if (value.State.HasValue) canonicalSnapshots.Add(value.State.Value);
        };
        context.Drain();
        snapshots.Clear();
        canonicalSnapshots.Clear();
        await using var stream = session.StreamStateAsync().GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        int generation = session.AcceptedClassificationGenerationForTests;
        context.Arm();
        Task worker = Task.Run(() => Publish(session, notification));
        try
        {
            Assert.True(context.Entered.Wait(TimeSpan.FromSeconds(5)), "The worker must reach its queued state notification.");
            now += TimeSpan.FromSeconds(1);
            if (change == "pause") await session.PauseClockAsync();
            else if (change == "resume") await session.ResumeClockAsync();
            else await session.OfferDrawAsync();
        }
        finally { context.Release.Set(); }
        await worker;
        context.Drain();

        Assert.Equal(generation, session.AcceptedClassificationGenerationForTests);
        Assert.NotEmpty(snapshots);
        Assert.NotEmpty(canonicalSnapshots);
        Assert.Equal(session.State.Clock, snapshots[^1].Clock);
        Assert.Equal(session.State.DrawOfferedBy, snapshots[^1].DrawOfferedBy);
        Assert.Equal(session.State.Clock, canonicalSnapshots[^1].Clock);
        Assert.Equal(session.State.DrawOfferedBy, canonicalSnapshots[^1].DrawOfferedBy);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(session.State.Clock, stream.Current.Clock);
        Assert.Equal(session.State.DrawOfferedBy, stream.Current.DrawOfferedBy);
    }

    [Fact]
    public async Task QueuedMetadataState_DoesNotOverwriteANewerEvaluationInTheSameGeneration()
    {
        var context = new BlockingQueuedContext();
        var options = new UciCoordinatorOptions
        {
            TimeControl = new PlayableMatchTimeControl(TimeSpan.FromMinutes(5), TimeSpan.Zero)
        };
        await using var session = new UciGameEngineSession(syncContext: context, options: options,
            utcNowProvider: () => DateTimeOffset.UnixEpoch);
        await session.LoadMatchAsync(PlayableMatchSetup.Standard);
        var snapshots = new List<UciState>();
        var canonicalSnapshots = new List<UciState>();
        session.StateChanged += snapshots.Add;
        session.EventPublished += value =>
        {
            if (value.Kind == UciGameEngineSessionEventKind.StateChanged && value.State.HasValue)
                canonicalSnapshots.Add(value.State.Value);
        };
        context.Drain();
        snapshots.Clear();
        canonicalSnapshots.Clear();
        context.Arm();
        Task pause = Task.Run(() => session.PauseClockAsync());
        try
        {
            Assert.True(context.Entered.Wait(TimeSpan.FromSeconds(5)), "The clock command must reach its queued state notification.");
            Publish(session, "evaluation");
        }
        finally { context.Release.Set(); }
        await pause;
        context.Drain();

        Assert.NotNull(session.State.Evaluation);
        Assert.NotEmpty(snapshots);
        Assert.NotEmpty(canonicalSnapshots);
        Assert.Equal(session.State.Evaluation, snapshots[^1].Evaluation);
        Assert.Equal(session.State.Evaluation, canonicalSnapshots[^1].Evaluation);
        Assert.True(snapshots[^1].Clock!.Value.IsPaused);
    }

    private static void Publish(UciGameEngineSession session, string notification)
    {
        int classification = session.AcceptedClassificationGenerationForTests;
        string notation = session.LegalMoves[0];
        switch (notification)
        {
            case "classification":
                var move = new Move(notation, MoveAnalysis.Analyze(notation, BoardState.FromFen(session.CurrentFen)!.Value,
                    MoveScore.FromCp(25), false));
                session.ApplyClassifiedMoveForTests(move, classification);
                break;
            case "completion":
                session.CompleteClassificationForTests(classification);
                break;
            case "evaluation":
                AcceptPonder(session);
                Assert.True(PrincipalVariation.TryParse("info depth 1 score cp 25 pv " + notation, out var pv));
                session.ApplyPonderInfoForTests(pv, 17);
                break;
            case "bestmove":
                AcceptPonder(session);
                session.ApplyPonderBestMoveForTests(ParsedMove.FromNotation(notation), null, 17);
                break;
            case "searching":
                typeof(UciGameEngineSession).GetMethod("SetBackgroundSearchingState", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(session, new object[] { classification });
                break;
            default: throw new ArgumentOutOfRangeException(nameof(notification));
        }
    }

    private static void AcceptPonder(UciGameEngineSession session) =>
        typeof(UciGameEngineSession).GetField("_acceptedPonderGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(session, 17);

    private sealed class BlockingQueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
        private int _armed;
        internal ManualResetEventSlim Entered { get; } = new(false);
        internal ManualResetEventSlim Release { get; } = new(false);
        internal void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The test did not release the blocked worker notification.");
            }
            _pending.Enqueue((callback, state));
        }

        internal void Drain()
        {
            while (_pending.TryDequeue(out var item)) item.Callback(item.State);
        }
    }
}

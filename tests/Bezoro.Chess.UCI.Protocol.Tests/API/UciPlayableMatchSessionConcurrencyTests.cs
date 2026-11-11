using System.Collections.Immutable;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.Tests.Domain;
using NSubstitute;

namespace Bezoro.Chess.UCI.Protocol.Tests.API;

public sealed class UciPlayableMatchSessionConcurrencyTests
{
    [Fact]
    public async Task PromotionCallbacksObserveTheCommittedCommandAndKeepTheirOriginalOrder()
    {
        using var session = new UciPlayableMatchSession();
        await session.LoadMatchAsync(new(Fen.Parse("7k/P7/8/8/8/8/8/K7 w - - 0 1")!.Value));
        session.ApplyMove("a7a8");
        var observations = new List<(PlayableMatchEventKind Kind, ImmutableArray<string> Moves)>();
        session.EventOccurred += e => observations.Add((e.Kind, session.PlayedMoves));

        session.ChoosePromotion('q');

        Assert.Equal(new[] { PlayableMatchEventKind.PromotionChosen, PlayableMatchEventKind.MoveApplied },
            observations.Select(o => o.Kind));
        Assert.All(observations, o => Assert.Equal(new[] { "a7a8q" }, o.Moves));
    }

    [Fact]
    public async Task MoveCallbacksCanJoinAnotherThreadsSnapshotReadWithoutHoldingTheStateGate()
    {
        using var session = new UciPlayableMatchSession();
        session.Refresh();
        Task<PlayableMatchSave>? read = null;
        bool joined = false;
        session.EventOccurred += e =>
        {
            if (e.Kind != PlayableMatchEventKind.MoveApplied) return;
            read = Task.Run(session.CaptureSave);
            joined = read.Wait(TimeSpan.FromSeconds(5));
        };

        session.ApplyMove("e2e4");

        Assert.True(joined);
        Assert.NotNull(read);
        Assert.Equal(new[] { "e2e4" }, (await read).Setup.PlayedMoves);
    }

    [Fact]
    public async Task BatchReceiptsExcludeEventsFromConcurrentCallers()
    {
        using var session = new UciPlayableMatchSession();
        session.Refresh();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int first = 0;
        session.EventOccurred += e =>
        {
            if (e.Kind != PlayableMatchEventKind.PositionRefreshed || Interlocked.Exchange(ref first, 1) != 0) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        };
        var batch = Task.Run(() => session.ProcessBatchAsync([new(PlayableMatchRequestKind.Refresh)]));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Run(() => session.Refresh()).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }

        var events = await batch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(events);
        Assert.Equal(PlayableMatchEventKind.PositionRefreshed, events[0].Kind);
    }

    [Fact]
    public async Task SubscriberSpawnedCommandsDoNotInheritTheOuterBatchCapture()
    {
        using var session = new UciPlayableMatchSession();
        session.Refresh();
        Task? child = null;
        bool joined = false;
        int first = 0;
        session.EventOccurred += e =>
        {
            if (Interlocked.Exchange(ref first, 1) == 0)
            {
                child = Task.Run(() => session.Refresh());
                joined = child.Wait(TimeSpan.FromSeconds(5));
            }
        };

        var receipt = await session.ProcessBatchAsync([new(PlayableMatchRequestKind.Refresh)]);
        Assert.True(joined);
        Assert.NotNull(child);
        await child.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(receipt);
    }

    [Fact]
    public void ThrowingSubscribersDoNotSuppressLaterObservers()
    {
        using var session = new UciPlayableMatchSession();
        int observed = 0;
        session.EventOccurred += _ => throw new InvalidOperationException("Subscriber failure");
        session.EventOccurred += _ => observed++;

        session.Refresh();

        Assert.Equal(1, observed);
    }

    [Fact]
    public void WarmSnapshotEventsReuseTheirBuffersAndSubscriberSnapshots()
    {
        using var session = new UciPlayableMatchSession();
        int observed = 0;
        session.EventOccurred += _ => observed++;
        for (int i = 0; i < 100; i++) session.Refresh();
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1000; i++) session.Refresh();

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(1100, observed);
    }

    [Fact]
    public async Task ConcurrentReplacementAndSaveReadsNeverPublishPartialHistories()
    {
        using var session = new UciPlayableMatchSession();
        await session.LoadMatchAsync(PlayableMatchSetup.Standard);
        PlayableMatchSetup[] setups = [new(Fen.Default, ["e2e4", "e7e5"]),
            new(Fen.Default, ["d2d4", "d7d5", "c2c4", "e7e6"])];
        using var start = new ManualResetEventSlim();
        var workers = Enumerable.Range(0, 4).Select(worker => Task.Run(async () =>
        {
            start.Wait();
            for (int i = 0; i < 100; i++)
                if ((worker & 1) == 0) await session.LoadMatchAsync(setups[(worker / 2 + i) % setups.Length]);
                else
                {
                    var save = session.CaptureSave();
                    var restored = save.Setup.ResolveCurrentFen();
                    restored.ValidateForPlay();
                    Assert.True(save.Setup.PlayedMoves.IsEmpty || setups.Any(s => s.PlayedMoves.SequenceEqual(save.Setup.PlayedMoves)));
                }
        })).ToArray();
        start.Set();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task InjectedClockCanJoinAnotherThreadsSnapshotReadOutsideTheStateGate()
    {
        UciPlayableMatchSession? session = null;
        Task<PlayableMatchState>? read = null;
        bool armed = false;
        bool joined = false;
        int observations = 0;
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset Observe()
        {
            observations++;
            if (armed)
            {
                armed = false;
                read = Task.Run(() => session!.CurrentState);
                joined = read.Wait(TimeSpan.FromSeconds(5));
            }
            return now;
        }
        using var lifetime = session = new(timeControl: new(TimeSpan.FromMinutes(5), TimeSpan.Zero), utcNowProvider: Observe);
        session.Refresh();
        armed = true;

        var save = session.CaptureSave();

        Assert.True(joined);
        Assert.NotNull(read);
        Assert.Equal(Fen.Default, (await read).Fen);
        Assert.Equal(2, observations);
        Assert.Empty(save.Setup.PlayedMoves);
    }

    [Fact]
    public async Task AClockObservationRetriesWhenAnotherClockCommandWinsAdmission()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int block = 0;
        var start = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        long ticks = start.Ticks;
        DateTimeOffset Observe()
        {
            if (Interlocked.Exchange(ref block, 0) == 1)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return start + TimeSpan.FromSeconds(1);
            }
            return new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        }
        using var session = new UciPlayableMatchSession(timeControl: new(TimeSpan.FromMinutes(5), TimeSpan.Zero), utcNowProvider: Observe);
        session.Refresh();
        block = 1;
        var older = Task.Run(session.Refresh);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Interlocked.Exchange(ref ticks, (start + TimeSpan.FromSeconds(3)).Ticks);
            session.PauseClock();
        }
        finally { release.Set(); }

        var state = await older.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(state.Clock!.Value.IsPaused);
        Assert.Equal(start + TimeSpan.FromSeconds(3), state.Clock.Value.SnapshotUtc);
        Assert.Equal(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(3), state.Clock.Value.WhiteRemaining);
    }

    [Fact]
    public async Task ThrowingAnalysisCancellationStillJoinsPlayingTurnsAndCompletesShutdown()
    {
        var (playingTransport, playingOutput) = UciEngineClientTestHelpers.CreateMockTransport();
        var (analysisTransport, analysisOutput) = UciEngineClientTestHelpers.CreateMockTransport();
        playingTransport.IsStarted.Returns(true);
        analysisTransport.IsStarted.Returns(true);
        playingTransport.When(t => t.WriteLineAsync("isready", Arg.Any<CancellationToken>())).Do(_ => playingOutput.Writer.TryWrite("readyok"));
        analysisTransport.When(t => t.WriteLineAsync("isready", Arg.Any<CancellationToken>())).Do(_ => analysisOutput.Writer.TryWrite("readyok"));
        var playingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var analysisStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("Native cancellation callback failed");
        CancellationTokenRegistration registration = default;
        playingTransport.When(t => t.WriteLineAsync(Arg.Is<string>(s => s.StartsWith("go ")), Arg.Any<CancellationToken>()))
            .Do(_ => playingStarted.TrySetResult());
        analysisTransport.When(t => t.WriteLineAsync(Arg.Is<string>(s => s.StartsWith("go ")), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                registration = ((CancellationToken)call[1]).Register(() => throw failure);
                analysisStarted.TrySetResult();
            });
        playingTransport.When(t => t.WriteLineAsync("stop", Arg.Any<CancellationToken>())).Do(_ => playingOutput.Writer.TryWrite("bestmove e2e4"));
        analysisTransport.When(t => t.WriteLineAsync("stop", Arg.Any<CancellationToken>())).Do(_ => analysisOutput.Writer.TryWrite("bestmove e2e4"));
        await using var playing = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(playingTransport, playingOutput);
        await using var analysis = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(analysisTransport, analysisOutput);
        var session = new UciPlayableMatchSession(playing, analysis, 'w', MatchSideControllerKind.Engine, MatchSideControllerKind.Engine);
        try
        {
            await session.LoadMatchAsync(PlayableMatchSetup.Standard);
            var reviewing = session.GetLegalMoveAnalysisAsync();
            await analysisStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var turn = session.PlayControlledMoveAsync();
            await playingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var error = await Assert.ThrowsAnyAsync<Exception>(() => session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.IsType<AggregateException>(error);
            Assert.Contains(failure, ((AggregateException)error).Flatten().InnerExceptions);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reviewing.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(session.PlayedMoves);
        }
        finally
        {
            registration.Dispose();
            try { await session.DisposeAsync(); }
            catch (AggregateException error) when (error.Flatten().InnerExceptions.Contains(failure)) { }
        }
    }

    [Fact]
    public async Task DisposalCancelsAndJoinsAnActiveTurnAndItsQueuedSuccessor()
    {
        var (transport, output) = UciEngineClientTestHelpers.CreateMockTransport();
        transport.IsStarted.Returns(true);
        transport.When(t => t.WriteLineAsync("isready", Arg.Any<CancellationToken>())).Do(_ => output.Writer.TryWrite("readyok"));
        var searched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.When(t => t.WriteLineAsync(Arg.Is<string>(s => s.StartsWith("go ")), Arg.Any<CancellationToken>()))
            .Do(_ => searched.TrySetResult());
        transport.When(t => t.WriteLineAsync("stop", Arg.Any<CancellationToken>())).Do(_ => output.Writer.TryWrite("bestmove e2e4"));
        await using var playing = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(transport, output);
        var (analysisTransport, analysisOutput) = UciEngineClientTestHelpers.CreateMockTransport();
        analysisTransport.When(t => t.WriteLineAsync("isready", Arg.Any<CancellationToken>())).Do(_ => analysisOutput.Writer.TryWrite("readyok"));
        await using var analysis = await UciEngineClientTestHelpers.StartClientWithHandshakeAsync(analysisTransport, analysisOutput);
        var session = new UciPlayableMatchSession(playing, analysis, 'w', MatchSideControllerKind.Engine,
            MatchSideControllerKind.Engine);
        try
        {
            await session.LoadMatchAsync(PlayableMatchSetup.Standard);
            var active = session.PlayControlledMoveAsync();
            await searched.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = session.PlayControlledMoveAsync();
            Assert.False(queued.IsCompleted);

            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Empty(session.PlayedMoves);
            Assert.True(playing.IsStarted);
        }
        finally { await session.DisposeAsync(); }
    }
}

using System.Reflection;
using System.Threading.Channels;
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.Protocol.Internal;
using Bezoro.Chess.UCI.Tests.Domain;
using NSubstitute;

namespace Bezoro.Chess.UCI.Tests.API;

public sealed class UciGameEngineSessionTaskTrackingTests
{
	[Fact]
	public async Task Gameplay_WithAutomaticAnalysisDisabled_LeavesIdleEnginesAlone()
	{
		var (session, ponderTransport) = CreateSession();
		await using var lifetime = session;
		await session.StartAsync();
		Assert.False(session.Options.EnableBackgroundAnalysis);
		ponderTransport.Transport.ClearReceivedCalls();

		for (int i = 0; i < 12; i++)
		{
			await session.ResetAsync();
			await DrainTrackedTasksAsync(session);
			await session.MakeMoveAsync("e2e4");
			await DrainTrackedTasksAsync(session);
			await session.UndoAsync();
			await DrainTrackedTasksAsync(session);
			await session.NewGameAsync();
			await DrainTrackedTasksAsync(session);
		}

		Assert.Empty(TrackedTasks(session));
		await ponderTransport.Transport.DidNotReceiveWithAnyArgs().WriteLineAsync(default!, default);
		await session.StopAsync();
		Assert.Empty(TrackedTasks(session));
	}

	[Fact]
	public async Task Dispose_WithPendingStopTasks_WaitsForAllTasksBeforeReleasingEngines()
	{
		var (session, ponderTransport) = CreateSession();
		await using var lifetime = session;
		await session.StartAsync();
		await session.StartSearchAsync();
		var blockedReady = ponderTransport.BlockNextReady();
		try
		{
			await session.ResetAsync();
			await blockedReady.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Task firstStop = Assert.Single(TrackedTasks(session));
			await session.MakeMoveAsync("e2e4");
			await session.UndoAsync();
			await session.NewGameAsync();

			Assert.Contains(firstStop, TrackedTasks(session));
			Assert.All(TrackedTasks(session), static task => Assert.False(task.IsCompleted));
			var disposal = session.DisposeAsync().AsTask();
			Assert.False(disposal.IsCompleted);
			Assert.True(ponderTransport.Transport.IsStarted);

			blockedReady.Release.TrySetResult();
			await disposal.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.False(ponderTransport.Transport.IsStarted);
			Assert.Empty(TrackedTasks(session));
		}
		finally { blockedReady.Release.TrySetResult(); }
	}

	[Fact]
	public async Task Gameplay_AfterAReportedWorkerFault_DropsTheCompletedWorkerAndStillDisposes()
	{
		var (session, ponderTransport) = CreateSession();
		await session.StartAsync();
		await session.StartSearchAsync();
		var writeFailure = new InvalidOperationException("controlled ready write failure");
		var subscriberFailure = new ApplicationException("controlled error subscriber failure");
		var blockedReady = ponderTransport.BlockNextReady(writeFailure);
		Exception? reportedError = null;
		void ThrowingSubscriber(Exception error)
		{
			reportedError = error;
			throw subscriberFailure;
		}
		session.Error += ThrowingSubscriber;
		try
		{
			await session.ResetAsync();
			await blockedReady.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Task failedWorker = Assert.Single(TrackedTasks(session));
			blockedReady.Release.TrySetResult();
			Assert.Same(subscriberFailure,
				await Assert.ThrowsAsync<ApplicationException>(() => failedWorker.WaitAsync(TimeSpan.FromSeconds(5))));
			Assert.Same(writeFailure, reportedError);
			session.Error -= ThrowingSubscriber;

			await session.ResetAsync();
			await DrainTrackedTasksAsync(session);
			Assert.DoesNotContain(failedWorker, TrackedTasks(session));
			await session.StopAsync();
			Assert.Empty(TrackedTasks(session));
		}
		finally
		{
			session.Error -= ThrowingSubscriber;
			blockedReady.Release.TrySetResult();
			try { await session.DisposeAsync(); }
			catch (ApplicationException error) when (ReferenceEquals(error, subscriberFailure)) { }
		}
	}

	private static Task[] TrackedTasks(UciGameEngineSession session) =>
		((List<Task>)typeof(UciGameEngineSession)
			.GetField("_analysisTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!).ToArray();

	private static Task DrainTrackedTasksAsync(UciGameEngineSession session) =>
		Task.WhenAll(TrackedTasks(session)).WaitAsync(TimeSpan.FromSeconds(5));

	private static (UciGameEngineSession Session, ControlledTransport Ponder) CreateSession()
	{
		var snapshot = new ControlledTransport();
		var ponder = new ControlledTransport();
		var classifier = new ControlledTransport();
		return (new UciGameEngineSession(new UciEngineClient(snapshot.Transport),
			new UciPonderRuntime(new UciEngineClient(ponder.Transport)),
			new UciEngineClient(classifier.Transport)), ponder);
	}

	private sealed class ControlledTransport
	{
		private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
		private int _started;
		private ReadyBarrier? _nextReady;

		internal ControlledTransport()
		{
			Transport = Substitute.For<IUciTransport>();
			Transport.IsStarted.Returns(_ => Volatile.Read(ref _started) != 0);
			Transport.IsHealthy.Returns(_ => Volatile.Read(ref _started) != 0);
			Transport.StartAsync(Arg.Any<CancellationToken>()).Returns(_ =>
			{
				Volatile.Write(ref _started, 1);
				return Task.CompletedTask;
			});
			Transport.StopAsync(Arg.Any<CancellationToken>()).Returns(_ =>
			{
				Volatile.Write(ref _started, 0);
				return Task.CompletedTask;
			});
			Transport.ReadLinesAsync(Arg.Any<CancellationToken>()).Returns(call =>
				UciEngineClientTestHelpers.StreamFromChannel(_lines.Reader, (CancellationToken)call[0]));
			Transport.WriteLineAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
				.Returns(call => WriteLineAsync((string)call[0]));
		}

		internal IUciTransport Transport { get; }

		internal ReadyBarrier BlockNextReady(Exception? failure = null)
		{
			var barrier = new ReadyBarrier(failure);
			Interlocked.Exchange(ref _nextReady, barrier);
			return barrier;
		}

		private async Task WriteLineAsync(string line)
		{
			if (line == "uci")
			{
				_lines.Writer.TryWrite("id name Controlled Engine");
				_lines.Writer.TryWrite("option name Threads type spin default 1 min 1 max 64");
				_lines.Writer.TryWrite("option name MultiPV type spin default 1 min 1 max 64");
				_lines.Writer.TryWrite("uciok");
			}
			else if (line == "isready")
			{
				if (Interlocked.Exchange(ref _nextReady, null) is { } barrier)
				{
					barrier.Entered.TrySetResult();
					await barrier.Release.Task.ConfigureAwait(false);
					if (barrier.Failure is { } failure) throw failure;
				}
				_lines.Writer.TryWrite("readyok");
			}
			else if (line == "stop")
				_lines.Writer.TryWrite("bestmove e2e4");
		}
	}

	private sealed class ReadyBarrier(Exception? failure)
	{
		internal Exception? Failure { get; } = failure;
		internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}
}

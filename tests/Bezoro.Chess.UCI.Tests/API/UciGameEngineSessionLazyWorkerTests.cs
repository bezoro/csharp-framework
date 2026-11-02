using System.Threading.Channels;
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Types;
using Bezoro.Chess.UCI.Protocol.Internal;
using Bezoro.Chess.UCI.Tests.Domain;
using NSubstitute;

namespace Bezoro.Chess.UCI.Tests.API;

public sealed class UciGameEngineSessionLazyWorkerTests
{
	[Fact]
	public async Task StartAndConfigure_UseOnlyThePrimaryEngineWhenBackgroundAnalysisIsDisabled()
	{
		var (session, primary, ponder, classifier) = Create();
		await using var lifetime = session;
		await session.StartAsync();
		await session.StartAsync();
		Assert.True(session.IsStarted);
		Assert.True(session.IsHealthy);
		Assert.Equal(20, session.LegalMoves.Count);
		Assert.Equal(1, primary.Starts);
		Assert.Equal(0, ponder.Starts);
		Assert.Equal(0, classifier.Starts);
		await session.SetOptionAsync("Hash", "32");
		await session.SetDebugAsync(true);
		Assert.Contains("setoption name Hash value 32", primary.Commands);
		Assert.Empty(ponder.Commands);
		Assert.Empty(classifier.Commands);
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		await session.SearchAsync(new() { Depth = 1 });
		await session.StopAsync();
		Assert.Equal(1, primary.Stops);
		Assert.Equal(0, ponder.Stops);
		Assert.Equal(0, classifier.Stops);
	}

	[Fact]
	public async Task ExplicitClassification_StartsOnlyItsWorkerAndIncludesStartedWorkersInHealth()
	{
		var (session, _, ponder, classifier) = Create();
		await using var lifetime = session;
		await session.StartAsync();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		await session.ClassifyMoveAsync("e2e4");
		await session.ClassifyMoveAsync("e2e4");
		Assert.Equal(1, classifier.Starts);
		Assert.Equal(0, ponder.Starts);
		Assert.True(session.IsHealthy);
		classifier.Healthy = false;
		Assert.False(session.IsHealthy);
		classifier.Healthy = true;
		await session.StopAsync();
		Assert.Equal(1, classifier.Stops);
		Assert.Equal(0, ponder.Stops);
	}

	[Fact]
	public async Task ExplicitContinuousSearch_StartsAndConfiguresOnlyThePonderWorker()
	{
		var (session, _, ponder, classifier) = Create();
		await using var lifetime = session;
		await session.StartAsync();
		await session.StartSearchAsync();
		await session.StopSearchAsync();
		await session.StartSearchAsync();
		Assert.Equal(1, ponder.Starts);
		Assert.Equal(0, classifier.Starts);
		Assert.Contains("setoption name Threads value 2", ponder.Commands);
		Assert.Contains("setoption name MultiPv value 1", ponder.Commands);
		await session.StopAsync();
	}

	[Fact]
	public async Task AutomaticAnalysis_ExplicitlyStartsBothWorkersAndRollsBackTheirStartupFailure()
	{
		var (session, primary, ponder, classifier) = Create(background: true);
		await using var lifetime = session;
		classifier.FailStart = true;
		await session.LoadMatchAsync(new(Fen.Default, ["e2e4"]));
		var position = session.CurrentFen;
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
		Assert.False(session.IsStarted);
		Assert.Equal(1, primary.Stops);
		Assert.Equal(1, ponder.Stops);
		Assert.Equal(position, session.CurrentFen);
	}

	[Fact]
	public async Task OnDemandWorkerStartupFailure_LeavesThePrimaryEngineAvailableForGameplay()
	{
		var (session, primary, ponder, classifier) = Create();
		await using var lifetime = session;
		await session.StartAsync();
		await session.LoadMatchAsync(PlayableMatchSetup.Standard);
		classifier.FailStart = true;
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.ClassifyMoveAsync("e2e4"));
		Assert.True(session.IsStarted);
		Assert.True(session.IsHealthy);
		Assert.Equal(0, primary.Stops);
		Assert.Equal(0, ponder.Starts);
		await session.MakeMoveAsync("e2e4");
		Assert.Single(session.PlayedMoves);
	}

	private static (UciGameEngineSession Session, Worker Primary, Worker Ponder, Worker Classifier) Create(bool background = false)
	{
		var primary = new Worker();
		var ponder = new Worker();
		var classifier = new Worker();
		return (new UciGameEngineSession(new UciEngineClient(primary.Transport),
			new UciPonderRuntime(new UciEngineClient(ponder.Transport)), new UciEngineClient(classifier.Transport),
			options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = background }), primary, ponder, classifier);
	}

	private sealed class Worker
	{
		private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
		private bool _started;
		internal readonly ConcurrentQueue<string> Commands = new();
		internal int Starts;
		internal int Stops;
		internal bool Healthy = true;
		internal bool FailStart;
		internal IUciTransport Transport { get; }

		internal Worker()
		{
			Transport = Substitute.For<IUciTransport>();
			Transport.IsStarted.Returns(_ => _started);
			Transport.IsHealthy.Returns(_ => _started && Healthy);
			Transport.StartAsync(Arg.Any<CancellationToken>()).Returns(_ =>
			{
				Starts++;
				if (FailStart) throw new InvalidOperationException("worker startup failed");
				_started = true;
				return Task.CompletedTask;
			});
			Transport.StopAsync(Arg.Any<CancellationToken>()).Returns(_ =>
			{
				Stops++;
				_started = false;
				return Task.CompletedTask;
			});
			Transport.ReadLinesAsync(Arg.Any<CancellationToken>()).Returns(call =>
				UciEngineClientTestHelpers.StreamFromChannel(_lines.Reader, (CancellationToken)call[0]));
			Transport.WriteLineAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
			{
				string command = (string)call[0];
				Commands.Enqueue(command);
				if (command == "uci") _lines.Writer.TryWrite("uciok");
				if (command == "isready") _lines.Writer.TryWrite("readyok");
				if (command.StartsWith("go ", StringComparison.Ordinal) && !command.Contains("infinite", StringComparison.Ordinal))
				{
					_lines.Writer.TryWrite("info depth 1 score cp 20 pv e2e4");
					_lines.Writer.TryWrite("bestmove e2e4");
				}
				if (command == "stop") _lines.Writer.TryWrite("bestmove e2e4");
				return Task.CompletedTask;
			});
		}
	}
}

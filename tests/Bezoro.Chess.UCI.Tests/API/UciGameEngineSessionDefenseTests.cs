using System.Threading.Channels;
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Common.Enums;
using Bezoro.Chess.UCI.API.Types;
using Bezoro.Chess.UCI.Protocol.Internal;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Tests.Attributes;
using Bezoro.Chess.UCI.Tests.Domain;
using Bezoro.Chess.UCI.Tests.TestHelpers;
using NSubstitute;

namespace Bezoro.Chess.UCI.Tests.API;

public sealed class UciGameEngineSessionDefenseTests
{
	[IntegrationTest]
	public async Task Defense_WithRealStockfishAndManualControllers_AppliesOneLocallyVerifiedPly()
	{
		await using var session = await UciGameEngineSession.CreateAsync(TestResourcePaths.STOCKFISH_PATH,
			options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = false, EngineMoveTimeMs = 50 },
			whiteController: MatchSideControllerKind.Manual, blackController: MatchSideControllerKind.Manual);
		await session.SetStrengthLimitAsync(1600);
		var before = session.State;

		var after = await session.PlayEngineDefenseAsync();

		string move = Assert.Single(after.PlayedMoves);
		Assert.Contains(move, before.LegalMoves);
		Assert.Equal(before.CurrentFen.ApplyMove(move).Raw, after.CurrentFen.Raw);
		Assert.Equal(after.CurrentFen, session.CurrentFen);
	}

	[Fact]
	public async Task TypedStrength_OnlyChangesThePlayingEngineEvenWhenAllWorkersAreStarted()
	{
		var (session, playing, ponder, analysis) = Create();
		await using var lifetime = session;
		await session.StartAsync();
		await session.StartSearchAsync();
		await session.StopSearchAsync();
		await session.ClassifyMoveAsync("e2e4");
		playing.Commands.Clear(); ponder.Commands.Clear(); analysis.Commands.Clear();

		await session.SetStrengthLimitAsync(1600);
		await session.ClearStrengthLimitAsync();

		Assert.Contains("setoption name UCI_LimitStrength value true", playing.Commands);
		Assert.Contains("setoption name UCI_Elo value 1600", playing.Commands);
		Assert.Contains("setoption name UCI_LimitStrength value false", playing.Commands);
		Assert.Empty(ponder.Commands);
		Assert.Empty(analysis.Commands);
	}

	[Fact]
	public async Task TypedStrength_RejectsUnsupportedAndOutOfRangeRequestsBeforeSendingOptions()
	{
		var (session, playing, _, _) = Create();
		await using var lifetime = session;
		await session.StartAsync();
		playing.Commands.Clear();
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.SetStrengthLimitAsync(500));
		Assert.Empty(playing.Commands);
		var (unsupported, noOptions, _, _) = Create(supportsStrength: false);
		await using var unsupportedLifetime = unsupported;
		await unsupported.StartAsync();
		noOptions.Commands.Clear();
		await Assert.ThrowsAsync<NotSupportedException>(() => unsupported.SetStrengthLimitAsync(1600));
		await unsupported.ClearStrengthLimitAsync();
		Assert.Empty(noOptions.Commands);
	}

	[Fact]
	public async Task Defense_OnAManualTurn_UsesUnlimitedAnalysisAndPublishesExactlyOneLegalEnginePly()
	{
		var (session, playing, ponder, analysis) = Create();
		await using var lifetime = session;
		await session.StartAsync();
		await session.SetStrengthLimitAsync(1600);
		playing.Commands.Clear();
		GameMoveEvent? applied = null;
		session.MoveMade += move => applied = move;

		var state = await session.PlayEngineDefenseAsync();

		Assert.Equal(MatchSideControllerKind.Manual, session.GetController('w'));
		Assert.Equal("e2e4", Assert.Single(state.PlayedMoves));
		Assert.Equal('b', state.CurrentFen.ActiveColor);
		Assert.Equal(GameMoveActor.Engine, applied!.Value.Actor);
		Assert.Contains("setoption name UCI_LimitStrength value false", analysis.Commands);
		Assert.Contains(analysis.Commands, command => command.StartsWith("go ", StringComparison.Ordinal));
		Assert.DoesNotContain(playing.Commands, command => command.StartsWith("go ", StringComparison.Ordinal));
		Assert.Empty(ponder.Commands);
	}

	[Theory]
	[InlineData("(none)")]
	[InlineData("e2e5")]
	public async Task Defense_InvalidEngineReply_DoesNotUseConfiguredLocalFallback(string reply)
	{
		var (session, _, _, analysis) = Create();
		await using var lifetime = session;
		await session.StartAsync();
		analysis.BestMove = reply;
		var initial = session.CurrentFen;

		await Assert.ThrowsAsync<InvalidOperationException>(() => session.PlayEngineDefenseAsync());

		Assert.Empty(session.PlayedMoves);
		Assert.Equal(initial, session.CurrentFen);
	}

	[Fact]
	public async Task Defense_IncompletePromotionReply_DoesNotLeaveAPromotionPromptOrClaimSuccess()
	{
		var (session, _, _, analysis) = Create();
		await using var lifetime = session;
		await session.StartAsync();
		await session.LoadMatchAsync(new(Fen.Parse("7k/P7/8/8/8/8/8/K7 w - - 0 1")!.Value));
		analysis.BestMove = "a7a8";
		var prompts = 0;
		session.PromotionRequired += _ => prompts++;

		await Assert.ThrowsAsync<InvalidOperationException>(() => session.PlayEngineDefenseAsync());

		Assert.Empty(session.PlayedMoves);
		Assert.Equal(0, prompts);
	}

	[Fact]
	public async Task Defense_CancellationReleasesTheCommandGateWithoutApplyingALateReplyToANewSetup()
	{
		var (session, _, _, analysis) = Create();
		await using var lifetime = session;
		await session.StartAsync();
		analysis.AutomaticReply = false;
		using var cancellation = new CancellationTokenSource();
		var defense = session.PlayEngineDefenseAsync(cancellation.Token);
		await analysis.SearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var newPosition = Fen.Parse("4k3/8/8/8/8/8/8/R3K3 w - - 0 1")!.Value;
		var load = session.LoadMatchAsync(new(newPosition));
		Assert.False(load.IsCompleted);
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => defense);
		await load.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Empty(session.PlayedMoves);
		Assert.Equal(newPosition.Raw, session.CurrentFen.Raw);
	}

	[Fact]
	public async Task Defense_ExpiredClockOrTerminalMatch_DoesNotStartAnEngineSearch()
	{
		var now = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
		var (session, _, _, analysis) = Create(() => now);
		await using var lifetime = session;
		await session.StartAsync();
		await session.ResumeClockAsync();
		now += TimeSpan.FromMinutes(10);

		await Assert.ThrowsAsync<InvalidOperationException>(() => session.PlayEngineDefenseAsync());
		Assert.Empty(analysis.Commands);
		Assert.Empty(session.PlayedMoves);
		await session.LoadMatchAsync(new(Fen.Parse("7k/6Q1/5K2/8/8/8/8/8 b - - 0 1")!.Value));
		await Assert.ThrowsAsync<InvalidOperationException>(() => session.PlayEngineDefenseAsync());
		Assert.Empty(analysis.Commands);
	}

	private static (UciGameEngineSession Session, Worker Playing, Worker Ponder, Worker Analysis) Create(
		Func<DateTimeOffset>? now = null, bool supportsStrength = true)
	{
		var playing = new Worker(supportsStrength);
		var ponder = new Worker(supportsStrength);
		var analysis = new Worker(supportsStrength);
		return (new UciGameEngineSession(new UciEngineClient(playing.Transport),
			new UciPonderRuntime(new UciEngineClient(ponder.Transport)), new UciEngineClient(analysis.Transport),
			options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = false,
				ControlledMoveFallbackPolicy = PlayableMatchControlledMoveFallbackPolicy.UseLocalFallback,
				TimeControl = new(TimeSpan.FromMinutes(5), TimeSpan.Zero) }, utcNowProvider: now), playing, ponder, analysis);
	}

	private sealed class Worker
	{
		private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
		private bool _started;
		internal readonly ConcurrentQueue<string> Commands = new();
		internal readonly TaskCompletionSource SearchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		internal string BestMove = "e2e4";
		internal bool AutomaticReply = true;
		internal IUciTransport Transport { get; }

		internal Worker(bool supportsStrength)
		{
			Transport = Substitute.For<IUciTransport>();
			Transport.IsStarted.Returns(_ => _started);
			Transport.IsHealthy.Returns(_ => _started);
			Transport.StartAsync(Arg.Any<CancellationToken>()).Returns(_ => { _started = true; return Task.CompletedTask; });
			Transport.StopAsync(Arg.Any<CancellationToken>()).Returns(_ => { _started = false; return Task.CompletedTask; });
			Transport.ReadLinesAsync(Arg.Any<CancellationToken>()).Returns(call =>
				UciEngineClientTestHelpers.StreamFromChannel(_lines.Reader, (CancellationToken)call[0]));
			Transport.WriteLineAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
			{
				string command = (string)call[0];
				Commands.Enqueue(command);
				if (command == "uci")
				{
					if (supportsStrength)
					{
						_lines.Writer.TryWrite("option name UCI_LimitStrength type check default false");
						_lines.Writer.TryWrite("option name UCI_Elo type spin default 2000 min 1000 max 3000");
					}
					_lines.Writer.TryWrite("uciok");
				}
				if (command == "isready") _lines.Writer.TryWrite("readyok");
				if (command.StartsWith("go ", StringComparison.Ordinal) && !command.Contains("infinite", StringComparison.Ordinal))
				{
					SearchStarted.TrySetResult();
					if (AutomaticReply) _lines.Writer.TryWrite("bestmove " + BestMove);
				}
				if (command == "stop") _lines.Writer.TryWrite("bestmove " + BestMove);
				return Task.CompletedTask;
			});
		}
	}
}

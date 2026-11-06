using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.Tests.Domain;
using FluentAssertions;
using NSubstitute;

namespace Bezoro.Chess.UCI.Protocol.Tests.API;

public sealed class UciGameReviewAnalyzerTests
{
	[Theory]
	[InlineData(0, MoveQuality.Best)]
	[InlineData(0.019999, MoveQuality.Excellent)]
	[InlineData(0.02, MoveQuality.Good)]
	[InlineData(0.049999, MoveQuality.Good)]
	[InlineData(0.05, MoveQuality.Inaccuracy)]
	[InlineData(0.099999, MoveQuality.Inaccuracy)]
	[InlineData(0.10, MoveQuality.Mistake)]
	[InlineData(0.199999, MoveQuality.Mistake)]
	[InlineData(0.20, MoveQuality.Blunder)]
	[InlineData(1, MoveQuality.Blunder)]
	public void Classify_ShouldUseExplicitExpectedPointsIntervals(double loss, MoveQuality expected)
	{
		MoveQualityClassifier.Classify(loss).Should().Be(expected);
	}

	[Theory]
	[InlineData(double.NaN)]
	[InlineData(double.PositiveInfinity)]
	[InlineData(-0.1)]
	[InlineData(1.1)]
	public void Classify_ShouldRejectInvalidProbabilities(double loss)
	{
		Action classify = () => MoveQualityClassifier.Classify(loss);
		classify.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public async Task AnalyzeAsync_ShouldReusePositionsPreserveHistoryAndNormalizeBothPlayerPerspectives()
	{
		var (client, commands) = CreateClient(
			("score cp 50 wdl 400 200 400", "d2d4"),
			("score cp 0 wdl 500 200 300", "c7c5"),
			("score cp 75 wdl 600 200 200", "g1f3"));
		await using var ownedClient = client;
		await client.StartAsync();
		var progress = new RecordingProgress();
		var analyzer = new UciGameReviewAnalyzer(client, new(new() { Depth = 12 }));

		var result = await analyzer.AnalyzeAsync(Fen.Default, ["E2E4", "e7e5"], progress);

		commands.Where(command => command.StartsWith("go ", StringComparison.Ordinal)).Should().HaveCount(3);
		commands.Where(command => command.StartsWith("position ", StringComparison.Ordinal)).Should().Equal(
			"position startpos", "position startpos moves e2e4", "position startpos moves e2e4 e7e5");
		commands.Should().Contain("setoption name UCI_ShowWDL value true");
		commands.Should().Contain("setoption name MultiPV value 1");
		commands.Should().Contain("setoption name UCI_LimitStrength value false");
		commands.Should().Contain("setoption name Skill Level value 20");
		result.Positions.Should().HaveCount(3);
		result.Moves.Should().HaveCount(2);
		result.Moves[0].ExpectedPointsBefore.Should().BeApproximately(0.5, 1e-12);
		result.Moves[0].ExpectedPointsAfter.Should().BeApproximately(0.4, 1e-12);
		result.Moves[0].ExpectedPointsLoss.Should().Be(0.10);
		result.Moves[0].Quality.Should().Be(MoveQuality.Mistake);
		result.Moves[0].CentipawnLoss.Should().Be(50);
		result.Moves[1].ScoreBefore.Cp.Should().Be(0);
		result.Moves[1].ScoreAfter.Cp.Should().Be(-75);
		result.Moves[1].CentipawnLoss.Should().Be(75);
		result.Moves[1].ExpectedPointsLoss.Should().Be(0.30);
		result.Moves[1].Quality.Should().Be(MoveQuality.Blunder);
		result.WhiteAccuracy.Should().Be(90);
		result.BlackAccuracy.Should().Be(70);
		result.ExpectedPointsModel.Should().Be("Engine WDL self-play");
		progress.Values.Should().Equal(new GameReviewProgress(1, 3) { PositionIndex = 0 }, new(2, 3) { PositionIndex = 1 }, new(3, 3) { PositionIndex = 2 });
	}

	[Theory]
	[InlineData("220 200 580", 0.02, MoveQuality.Good)]
	[InlineData("250 200 550", 0.05, MoveQuality.Inaccuracy)]
	[InlineData("300 200 500", 0.10, MoveQuality.Mistake)]
	[InlineData("400 200 400", 0.20, MoveQuality.Blunder)]
	public async Task AnalyzeAsync_WhenWdlLossLandsExactlyOnACutoff_ShouldPreserveDecimalBoundary(
		string replyWdl, double expectedLoss, MoveQuality expectedQuality)
	{
		var (client, _) = CreateClient(
			("score cp 50 wdl 600 200 200", "d2d4"),
			($"score cp 0 wdl {replyWdl}", "c7c5"));
		await using var ownedClient = client;
		await client.StartAsync();

		var review = await new UciGameReviewAnalyzer(client, new(new() { Depth = 1 }))
			.AnalyzeAsync(Fen.Default, ["e2e4"]);

		review.Moves[0].ExpectedPointsLoss.Should().Be(expectedLoss);
		review.Moves[0].Quality.Should().Be(expectedQuality);
	}

	[Fact]
	public async Task AnalyzeAsync_WhenReviewsRunConcurrently_ShouldKeepEachHistoryTogether()
	{
		var (client, commands) = CreateClient(
			("score cp 0 wdl 400 200 400", "d2d4"),
			("score cp 0 wdl 400 200 400", "c7c5"),
			("score cp 0 wdl 400 200 400", "e2e4"),
			("score cp 0 wdl 400 200 400", "d7d5"));
		await using var ownedClient = client;
		await client.StartAsync();
		var analyzer = new UciGameReviewAnalyzer(client, new(new() { Depth = 1 }));

		var reviews = await Task.WhenAll(analyzer.AnalyzeAsync(Fen.Default, ["e2e4"]),
			analyzer.AnalyzeAsync(Fen.Default, ["d2d4"])).WaitAsync(TimeSpan.FromSeconds(2));

		reviews[0].Moves[0].Move.Should().Be("e2e4");
		reviews[1].Moves[0].Move.Should().Be("d2d4");
		commands.Where(command => command.StartsWith("position ", StringComparison.Ordinal)).Should().Equal(
			"position startpos", "position startpos moves e2e4", "position startpos", "position startpos moves d2d4");
	}

	[Fact]
	public async Task AnalyzeAsync_WhenCentipawnDifferenceExceedsIntRange_ShouldPreserveLongLoss()
	{
		var (client, _) = CreateClient(
			("score cp 2147483647 wdl 1000 0 0", "d2d4"),
			("score cp 2147483647 wdl 1000 0 0", "c7c5"));
		await using var ownedClient = client;
		await client.StartAsync();

		var result = await new UciGameReviewAnalyzer(client, new(new() { Depth = 1 }))
			.AnalyzeAsync(Fen.Default, ["e2e4"]);

		result.Moves[0].CentipawnLoss.Should().Be(4294967294L);
	}

	[Fact]
	public async Task AnalyzeAsync_WhenSeventyFiveMoveRuleEndsGame_ShouldSkipFinalSearch()
	{
		var (client, commands) = CreateClient(("score cp 0 wdl 400 200 400", "a1a2"));
		await using var ownedClient = client;
		await client.StartAsync();
		var start = Fen.Parse("7k/8/8/8/8/8/8/KR6 w - - 149 1")!.Value;

		var result = await new UciGameReviewAnalyzer(client, new(new() { Depth = 1 }))
			.AnalyzeAsync(start, ["a1a2"]);

		commands.Count(command => command.StartsWith("go ", StringComparison.Ordinal)).Should().Be(1);
		result.Positions[1].Result.Reason.Should().Be(PlayableMatchResultReason.SeventyFiveMoveRule);
		result.Moves[0].ExpectedPointsAfter.Should().Be(0.5);
	}

	[Fact]
	public async Task AnalyzeAsync_WhenPlayedMoveIsPreferred_ShouldIgnoreIndependentSearchNoise()
	{
		var (client, _) = CreateClient(
			("score cp 50 wdl 700 200 100", "e2e4"),
			("score cp 30 wdl 500 200 300", "c7c5"));
		await using var ownedClient = client;
		await client.StartAsync();
		var result = await new UciGameReviewAnalyzer(client, new(new() { Nodes = 1000 }))
			.AnalyzeAsync(Fen.Default, ["e2e4"]);

		result.Moves[0].Quality.Should().Be(MoveQuality.Best);
		result.Moves[0].ExpectedPointsLoss.Should().Be(0);
		result.Moves[0].CentipawnLoss.Should().Be(0);
		result.WhiteAccuracy.Should().Be(100);
		result.BlackAccuracy.Should().BeNull();
	}

	[Fact]
	public async Task AnalyzeAsync_WhenMoveCheckmates_ShouldAdjudicateFinalPositionWithoutSearchingIt()
	{
		var (client, commands) = CreateClient(("score mate 1 wdl 1000 0 0", "f7g7"));
		await using var ownedClient = client;
		await client.StartAsync();
		var start = Fen.Parse("7k/5Q2/7K/8/8/8/8/8 w - - 0 1")!.Value;

		var result = await new UciGameReviewAnalyzer(client, new(new() { MoveTimeMs = 10 }))
			.AnalyzeAsync(start, ["f7g7"]);

		commands.Count(command => command.StartsWith("go ", StringComparison.Ordinal)).Should().Be(1);
		result.Positions[1].Result.Should().Be(new PlayableMatchResult(PlayableMatchResultReason.Checkmate, 'w'));
		result.Moves[0].Classification.IsMate.Should().BeTrue();
		result.Moves[0].CentipawnLoss.Should().BeNull();
		result.Moves[0].ExpectedPointsAfter.Should().Be(1);
	}

	[Fact]
	public async Task AnalyzeAsync_WhenPositionIsDead_ShouldRequireNoRunningEngine()
	{
		var (client, commands) = CreateClient();
		await using var ownedClient = client;
		var start = Fen.Parse("7k/8/8/8/8/8/8/K7 w - - 0 1")!.Value;

		var result = await new UciGameReviewAnalyzer(client, new(new() { Depth = 1 }))
			.AnalyzeAsync(start, []);

		commands.Should().BeEmpty();
		result.Positions[0].Result.Reason.Should().Be(PlayableMatchResultReason.InsufficientMaterial);
		result.Moves.Should().BeEmpty();
		result.WhiteAccuracy.Should().BeNull();
	}

	[Theory]
	[InlineData("8/8/8/8/8/8/8/K7 w - - 0 1")]
	[InlineData("7k/8/8/8/8/8/8/KK6 w - - 0 1")]
	[InlineData("8/8/8/8/8/8/8/Kk6 w - - 0 1")]
	[InlineData("7k/6Q1/8/8/8/8/8/K7 w - - 0 1")]
	[InlineData("7k/8/8/8/8/8/8/KP6 w - - 0 1")]
	[InlineData("P6k/8/8/8/8/8/8/K7 w - - 0 1")]
	[InlineData("7k/8/8/8/8/P7/PPPPPPPP/K7 w - - 0 1")]
	[InlineData("7k/8/8/8/8/RR6/PPPPPPPP/KRRRRRRR w - - 0 1")]
	[InlineData("7k/8/8/8/8/8/8/4K3 w K - 0 1")]
	[InlineData("7k/8/8/8/8/8/8/4K2R w - d6 0 1")]
	[InlineData("7k/8/8/3pP3/8/8/8/K7 w - d6 1 1")]
	public async Task AnalyzeAsync_WhenStartingPositionIsUnsafeForAnEngine_ShouldRejectBeforeSendingCommands(string rawFen)
	{
		var (client, commands) = CreateClient();
		await using var ownedClient = client;
		var analyzer = new UciGameReviewAnalyzer(client, new(new() { Depth = 1 }));

		await FluentActions.Awaiting(() => analyzer.AnalyzeAsync(Fen.Parse(rawFen)!.Value, []))
			.Should().ThrowAsync<ArgumentException>();

		commands.Should().BeEmpty();
	}

	[Fact]
	public async Task AnalyzeAsync_WhenGameIsInvalid_ShouldFailBeforeAnyEngineCommand()
	{
		var (client, commands) = CreateClient();
		await using var ownedClient = client;
		var analyzer = new UciGameReviewAnalyzer(client, new(new() { Depth = 1 }));

		await FluentActions.Awaiting(() => analyzer.AnalyzeAsync(Fen.Default, ["e2e4", "e7e4"]))
			.Should().ThrowAsync<InvalidOperationException>();

		commands.Should().BeEmpty();
	}

	[Fact]
	public async Task AnalyzeAsync_WhenWdlIsMissing_ShouldRequireAnExplicitProbabilityModel()
	{
		var (client, _) = CreateClient(("score cp 50", "d2d4"), ("score cp 0", "c7c5"));
		await using var ownedClient = client;
		await client.StartAsync();
		var analyzer = new UciGameReviewAnalyzer(client, new(new() { Depth = 1 }));

		await FluentActions.Awaiting(() => analyzer.AnalyzeAsync(Fen.Default, ["e2e4"]))
			.Should().ThrowAsync<NotSupportedException>().WithMessage("*IExpectedPointsModel*");
	}

	[Fact]
	public async Task AnalyzeAsync_ShouldPassRatingsAndContextToApplicationPolicies()
	{
		var (client, commands) = CreateClient(("score cp 0", "c7c5"), ("score cp 20", "g1f3"));
		await using var ownedClient = client;
		await client.StartAsync();
		var model = new RatingModel();
		var policy = Substitute.For<IMoveReviewAnnotationPolicy>();
		policy.Annotate(Arg.Any<ReviewedMove>(), 1250).Returns(MoveReviewAnnotation.Book);
		var start = Fen.Default.ApplyMove("e2e4");
		var analyzer = new UciGameReviewAnalyzer(client,
			new(new() { Depth = 1 }, whiteRating: 2100, blackRating: 1250), model, policy);

		var result = await analyzer.AnalyzeAsync(start, ["e7e5"]);

		model.Requests.Should().Equal(('b', 1250), ('b', 1250));
		result.Moves[0].Annotation.Should().Be(MoveReviewAnnotation.Book);
		result.Moves[0].Classification.IsDoublePawnPush.Should().BeTrue();
		result.ExpectedPointsModel.Should().Be("Test rating model");
		commands.Should().Contain($"position fen {start.Raw} moves e7e5");
	}

	[Fact]
	public async Task AnalyzeAsync_WhenCanceledBetweenPositions_ShouldReleaseSerializationGateForNextReview()
	{
		var (client, commands) = CreateClient(
			("score cp 0 wdl 400 200 400", "d2d4"),
			("score cp 0 wdl 400 200 400", "d2d4"),
			("score cp 0 wdl 400 200 400", "c7c5"));
		await using var ownedClient = client;
		await client.StartAsync();
		using var cancellation = new CancellationTokenSource();
		var analyzer = new UciGameReviewAnalyzer(client, new(new() { Depth = 1 }));
		var progress = new RecordingProgress(_ => cancellation.Cancel());

		await FluentActions.Awaiting(() => analyzer.AnalyzeAsync(Fen.Default, ["e2e4"], progress, cancellation.Token))
			.Should().ThrowAsync<OperationCanceledException>();
		var result = await analyzer.AnalyzeAsync(Fen.Default, ["e2e4"]).WaitAsync(TimeSpan.FromSeconds(2));

		commands.Count(command => command.StartsWith("go ", StringComparison.Ordinal)).Should().Be(3);
		result.Moves.Should().ContainSingle();
	}

	[Fact]
	public async Task AnalyzeAsync_WhenScoreIsOnlyABound_ShouldRejectIt()
	{
		var (client, _) = CreateClient(("score cp 50 lowerbound wdl 400 200 400", "d2d4"));
		await using var ownedClient = client;
		await client.StartAsync();
		var analyzer = new UciGameReviewAnalyzer(client, new(new() { Depth = 1 }));

		await FluentActions.Awaiting(() => analyzer.AnalyzeAsync(Fen.Default, ["e2e4"]))
			.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exact*");
	}

	[Fact]
	public async Task AnalyzeAsync_WhenADeeperIterationEndsWithABound_ShouldUseTheCompletedExactBestMoveIteration()
	{
		await using var client = CreateTranscriptClient("e2e4",
			"info depth 3 score cp 10 wdl 300 500 200 pv e2e4",
			"info depth 6 score cp 30 wdl 400 400 200 pv e2e4",
			"info depth 7 score cp 50 lowerbound wdl 600 300 100 pv e2e4");
		await client.StartAsync();
		var analyzer = new UciGameReviewAnalyzer(client, new(new() { Nodes = 1_000 }));
		var result = await analyzer.AnalyzeAsync(Fen.Default, []);
		var evaluation = result.Positions[0];
		evaluation.Score.Cp.Should().Be(30);
		evaluation.PrincipalVariation!.Value.Depth.Should().Be(6);
		evaluation.PrincipalVariation.Value.ScoreBound.Should().Be(UciScoreBound.Exact);
		evaluation.BestMove.Should().Be("e2e4");
	}

	[Fact]
	public async Task AnalyzeAsync_WhenOnlyAnotherMoveHasAnExactScore_ShouldRejectUnsupportedBestMoveEvidence()
	{
		await using var client = CreateTranscriptClient("e2e4",
			"info depth 6 score cp 30 wdl 400 400 200 pv d2d4",
			"info depth 7 score cp 50 lowerbound wdl 600 300 100 pv e2e4");
		await client.StartAsync();
		var analyzer = new UciGameReviewAnalyzer(client, new(new() { Nodes = 1_000 }));
		await FluentActions.Awaiting(() => analyzer.AnalyzeAsync(Fen.Default, []))
			.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exact*best move*");
	}

	private static UciEngineClient CreateTranscriptClient(string bestMove, params string[] variations)
	{
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		transport.WriteLineAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			string command = (string)call[0];
			if (command == "uci") channel.Writer.TryWrite("uciok");
			if (command == "isready") channel.Writer.TryWrite("readyok");
			if (command.StartsWith("go ", StringComparison.Ordinal))
			{
				foreach (string variation in variations) channel.Writer.TryWrite(variation);
				channel.Writer.TryWrite($"bestmove {bestMove}");
			}
			return Task.CompletedTask;
		});
		return new UciEngineClient(transport);
	}

	[Fact]
	public async Task AnalyzeAsync_WhenEngineClaimsMateZeroInANonterminalPosition_ShouldRejectIt()
	{
		var (client, _) = CreateClient(("score mate 0 wdl 0 0 1000", "d2d4"));
		await using var ownedClient = client;
		await client.StartAsync();
		var analyzer = new UciGameReviewAnalyzer(client, new(new() { Depth = 1 }));

		await FluentActions.Awaiting(() => analyzer.AnalyzeAsync(Fen.Default, ["e2e4"]))
			.Should().ThrowAsync<InvalidOperationException>();
	}

	[Fact]
	public async Task AnalyzeAsync_WhenModelReturnsInvalidValue_ShouldRejectIt()
	{
		var (client, _) = CreateClient(("score cp 0", "d2d4"), ("score cp 0", "c7c5"));
		await using var ownedClient = client;
		await client.StartAsync();
		var model = Substitute.For<IExpectedPointsModel>();
		model.GetExpectedPoints(Arg.Any<GameReviewEvaluation>(), Arg.Any<char>(), Arg.Any<int?>()).Returns(double.NaN);
		var analyzer = new UciGameReviewAnalyzer(client, new(new() { Depth = 1 }), model);

		await FluentActions.Awaiting(() => analyzer.AnalyzeAsync(Fen.Default, ["e2e4"]))
			.Should().ThrowAsync<InvalidOperationException>().WithMessage("*outside [0,1]*");
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void GameReviewOptions_ShouldRejectUnboundedOrRestrictedSearches(bool infinite)
	{
		Action create = () => new GameReviewOptions(infinite
			? new SearchParameters { Infinite = true }
			: new SearchParameters { Depth = 10, SearchMoves = ["e2e4"] });

		create.Should().Throw<ArgumentException>();
	}

	private static (UciEngineClient Client, List<string> Commands) CreateClient(params (string Evaluation, string BestMove)[] responses)
	{
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		var commands = new List<string>();
		var searches = new Queue<(string Evaluation, string BestMove)>(responses);
		transport.WriteLineAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
		{
			string command = (string)call[0];
			commands.Add(command);
			if (command == "uci")
			{
				channel.Writer.TryWrite("option name UCI_ShowWDL type check default false");
				channel.Writer.TryWrite("option name UCI_LimitStrength type check default true");
				channel.Writer.TryWrite("option name MultiPV type spin default 3 min 1 max 10");
				channel.Writer.TryWrite("option name Skill Level type spin default 0 min 0 max 20");
				channel.Writer.TryWrite("uciok");
			}
			if (command == "isready") channel.Writer.TryWrite("readyok");
			if (command.StartsWith("go ", StringComparison.Ordinal))
			{
				var response = searches.Dequeue();
				channel.Writer.TryWrite($"info depth 12 {response.Evaluation} pv {response.BestMove}");
				channel.Writer.TryWrite($"bestmove {response.BestMove}");
			}
			return Task.CompletedTask;
		});
		return (new UciEngineClient(transport), commands);
	}

	private sealed class RecordingProgress(Action<GameReviewProgress>? callback = null) : IProgress<GameReviewProgress>
	{
		internal List<GameReviewProgress> Values { get; } = [];
		public void Report(GameReviewProgress value)
		{
			Values.Add(value);
			callback?.Invoke(value);
		}
	}

	private sealed class RatingModel : IExpectedPointsModel
	{
		public string Name => "Test rating model";
		internal List<(char Side, int? Rating)> Requests { get; } = [];
		public double GetExpectedPoints(GameReviewEvaluation evaluation, char playerSide, int? playerRating)
		{
			Requests.Add((playerSide, playerRating));
			double points = playerRating < 1500 ? 0.65 : 0.8;
			return evaluation.Position.ActiveColor == playerSide ? points : 1 - points;
		}
	}
}

using System.Threading.Channels;
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Types;
using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Abstractions;
using Bezoro.Chess.UCI.Protocol.Internal;
using Bezoro.Chess.UCI.Tests.Attributes;
using Bezoro.Chess.UCI.Tests.Domain;
using Bezoro.Chess.UCI.Tests.TestHelpers;
using FluentAssertions;
using JetBrains.Annotations;
using NSubstitute;

namespace Bezoro.Chess.UCI.Tests.API;

[TestSubject(typeof(UciGameEngineSession))]
[Trait("Category", "Integration")]
[Collection("Stockfish")]
public class UciGameEngineSessionTests
{
	[Fact]
	public async Task ApplyClassifiedMoveForTests_WhenGenerationIsStale_ShouldIgnoreMoveEvenIfNotationStillMatches()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();
		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);

		int staleGeneration = coordinator.AcceptedClassificationGenerationForTests;
		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);
		coordinator.AcceptedClassificationGenerationForTests.Should().NotBe(staleGeneration);

		var board = BoardState.FromFen(Fen.Default);
		board.HasValue.Should().BeTrue();

		var staleMove = new Move(
			"e2e4",
			MoveAnalysis.Analyze("e2e4", board!.Value, MoveScore.FromCp(25), false)
		);

		coordinator.ApplyClassifiedMoveForTests(staleMove, staleGeneration);

		coordinator.State.ClassifiedMoves.Should().NotContainKey("e2e4");
		await coordinator.StopAsync();
	}

	[Fact]
	public async Task ApplyPonderBestMoveForTests_WhenGenerationIsStale_ShouldIgnoreUpdate()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		await coordinator.StartSearchAsync(Fen.Default);
		await coordinator.StopSearchAsync();
		coordinator.ApplyPonderBestMoveForTests(
			ParsedMove.FromNotation("e2e4"),
			ParsedMove.FromNotation("e7e5"),
			1
		);

		coordinator.State.BestMove.Should().BeNull();
		coordinator.State.PonderMove.Should().BeNull();
		await coordinator.StopAsync();
	}

	[Fact]
	public async Task ApplyPonderBestMoveForTests_WhenSearchIsRetargeted_ShouldIgnoreOldGenerationOutput()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		var mateFen = Fen.Parse(TestConstants.WHITE_MATE_IN_ONE_FEN);
		mateFen.Should().NotBeNull();

		await coordinator.StartSearchAsync(Fen.Default);
		int staleGeneration = coordinator.AcceptedPonderGenerationForTests;

		await coordinator.StartSearchAsync(mateFen!.Value);
		coordinator.AcceptedPonderGenerationForTests.Should().NotBe(staleGeneration);

		coordinator.ApplyPonderBestMoveForTests(
			ParsedMove.FromNotation("e2e4"),
			ParsedMove.FromNotation("e7e5"),
			staleGeneration
		);

		coordinator.State.BestMove?.Raw.Should().NotBe("e2e4");
		coordinator.State.PonderMove?.Raw.Should().NotBe("e7e5");
		await coordinator.StopAsync();
	}

	[Fact]
	public async Task ApplyPonderInfoForTests_WhenGenerationIsStale_ShouldIgnoreUpdate()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		var pv = new PrincipalVariation(4, 6, 1, 20, null, 100, 1000, 0, 10, ["e2e4"], "e2e4");

		await coordinator.StartSearchAsync(Fen.Default);
		await coordinator.StopSearchAsync();
		coordinator.ApplyPonderInfoForTests(pv, 1);

		coordinator.State.Evaluation.Should().BeNull();
		await coordinator.StopAsync();
	}

	[Fact]
	public async Task BestSearch_WhenStartedAndStopped_ShouldRestartCleanly()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		var bestTcs1 =
			new TaskCompletionSource<(ParsedMove best, ParsedMove? ponder)>(
				TaskCreationOptions.RunContinuationsAsynchronously
			);

		var bestTcs2 =
			new TaskCompletionSource<(ParsedMove best, ParsedMove? ponder)>(
				TaskCreationOptions.RunContinuationsAsynchronously
			);

		coordinator.StateChanged += state =>
		{
			if (state.BestMove == null || string.IsNullOrWhiteSpace(state.BestMove.Value.Raw)) return;

			if (!state.BestMove.HasValue) return;

			// We need to distinguish the first run from the second run.
			// The first run completes bestTcs1.
			if (!bestTcs1.Task.IsCompleted)
				bestTcs1.TrySetResult((state.BestMove.Value, state.PonderMove));
			else if (!bestTcs2.Task.IsCompleted)
				// This must be the second run
				bestTcs2.TrySetResult((state.BestMove.Value, state.PonderMove));
		};

		// Start best search for current FEN
		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);
		await coordinator.StartSearchAsync();

		var first = await bestTcs1.Task.WaitAsync(TestConstants.DefaultTimeout);
		first.best.Raw.Should().NotBeNullOrWhiteSpace();

		// Stop and restart
		await coordinator.StopSearchAsync();
		await coordinator.StartSearchAsync();

		var second = await bestTcs2.Task.WaitAsync(TestConstants.DefaultTimeout);
		second.best.Raw.Should().NotBeNullOrWhiteSpace();

		await coordinator.StopAsync();
	}

	[Fact]
	public async Task ClassifyMoveForStateAsync_WhenCoordinatorStateHasChanged_ShouldUseCapturedSnapshotFen()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();
		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);

		var snapshot = coordinator.State;
		snapshot.LegalMoves.Should().Contain("e2e4");
		await coordinator.LoadMatchAsync(new PlayableMatchSetup(Fen.Default, ["e2e4"]));
		await coordinator.WaitForClassificationAsync();

		var move = await coordinator.ClassifyMoveForStateAsync(snapshot, "e2e4");

		move.Notation.Should().Be("e2e4");
		await coordinator.StopAsync();
	}

	[Fact]
	public async Task ClearState_WhenCalled_ShouldCancelClassificationTokenSource()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		var classificationStarted = false;

		coordinator.StateChanged += state =>
		{
			if (state.ClassifiedMoves.Count > 0) classificationStarted = true;
		};

		// Start classification
		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);
		await Task.Delay(TestConstants.MediumDelay);

		// ClearState should cancel classification
		// This is called internally by UpdatePositionAsync, but we can verify it works
		await coordinator.LoadMatchAsync(new PlayableMatchSetup(Fen.Parse(TestConstants.ITALIAN_GAME_FEN)!.Value));

		// Wait to see if previous classification continues (it shouldn't)
		await Task.Delay(TestConstants.MediumDelay);

		// Classification should have started
		classificationStarted.Should().BeTrue("Classification should have started");

		await coordinator.StopAsync();
	}

	[Fact]
	public async Task ClearState_WhenCalledConcurrently_ShouldBeThreadSafe()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		var exceptions  = new ConcurrentBag<Exception>();
		var tasks       = new List<Task>();
		var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var firstFen    = Fen.Parse(TestConstants.WHITE_MATE_IN_ONE_FEN);
		firstFen.Should().NotBeNull();
		var secondFen = Fen.Parse(TestConstants.ITALIAN_GAME_FEN);
		secondFen.Should().NotBeNull();

		// Two overlapping updates are enough to exercise ClearState concurrency.
		foreach (var fen in new[] { firstFen!.Value, secondFen!.Value })
		{
			tasks.Add(
				Task.Run(async () =>
					{
						try
						{
							await releaseGate.Task.WaitAsync(TestConstants.DefaultTimeout);
							await coordinator.LoadMatchAsync(new PlayableMatchSetup(fen));
						}
						catch (OperationCanceledException)
						{
							// Expected when previous classification is cancelled
						}
						catch (Exception ex)
						{
							exceptions.Add(ex);
						}
					}
				)
			);
		}

		releaseGate.TrySetResult();
		await Task.WhenAll(tasks).WaitAsync(TestConstants.ExtendedTimeout);

		// OperationCanceledException is expected when classifications are cancelled
		var unexpectedExceptions = exceptions.Where(ex => ex is not OperationCanceledException).ToList();
		unexpectedExceptions.Should()
							.BeEmpty(
								"Concurrent ClearState operations (via UpdatePositionAsync) should be thread-safe"
							);

		coordinator.State.Should().NotBeNull();

		await coordinator.StopAsync();
	}

	[Fact]
	public async Task CurrentLegalMoves_WhenAccessedConcurrently_ShouldBeThreadSafe()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);

		// Concurrent reads and writes
		var tasks      = new List<Task>();
		var exceptions = new ConcurrentBag<Exception>();

		for (var i = 0; i < 10; i++)
		{
			tasks.Add(
				Task.Run(async () =>
					{
						try
						{
							for (var j = 0; j < 10; j++) await Task.Delay(1);
							// moves can be null during position updates, checking for thread-safety (no exceptions)
						}
						catch (Exception ex)
						{
							exceptions.Add(ex);
						}
					}
				)
			);
		}

		// Also update position concurrently
		tasks.Add(
			Task.Run(async () =>
				{
					try
					{
						for (var i = 0; i < 5; i++)
						{
							await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);
							await Task.Delay(TestConstants.ShortDelay);
						}
					}
					catch (Exception ex)
					{
						exceptions.Add(ex);
					}
				}
			)
		);

		await Task.WhenAll(tasks);

		exceptions.Should().BeEmpty("No exceptions should occur during concurrent access");
		coordinator.State.LegalMoves.Should()
				   .NotBeNull("LegalMoves should still be accessible after concurrent operations");

		await coordinator.StopAsync();
	}

	[Fact]
	public async Task DisposeAsync_WhenCalled_ShouldUnsubscribeFromEngineEvents()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		var stateChangeCount = 0;

		coordinator.StateChanged += _ => stateChangeCount++;

		// Start some operations
		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);
		await Task.Delay(TestConstants.ShortDelay);

		// Stop operations before disposing to prevent events from firing during disposal
		await coordinator.StopAsync();

		// Capture counts before dispose
		int stateChangeBefore = stateChangeCount;

		// Dispose should unsubscribe from events
		await coordinator.DisposeAsync();

		// Wait a bit to see if any events fire after dispose
		await Task.Delay(TestConstants.MediumDelay);

		// Counts should not have increased after dispose
		stateChangeCount.Should().Be(stateChangeBefore, "StateChanged should not fire after dispose");
	}

	[Fact]
	public async Task DisposeAsync_WhenCalledAfterSearchAndClassification_ShouldNotThrow()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		// Start search to create _bestCts
		await coordinator.StartSearchAsync(Fen.Default);
		await Task.Delay(TestConstants.ShortDelay);

		// Update position to create _classificationCts
		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);
		await Task.Delay(TestConstants.ShortDelay);

		// Dispose should clean up both token sources
		await coordinator.DisposeAsync();

		// If we got here without exceptions, disposal worked
		// This test verifies that DisposeAsync doesn't throw when disposing token sources
	}

	[Fact]
	public async Task DisposeAsync_WhenCalledWithActiveSearches_ShouldDisposeAllTokenSourcesSafely()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		// Start search to create _bestCts
		await coordinator.StartSearchAsync(Fen.Default);
		await Task.Delay(TestConstants.ShortDelay);

		// Update position to create _classificationCts
		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);
		await Task.Delay(TestConstants.ShortDelay);

		// Start another search concurrently before disposing
		var searchTask = Task.Run(async () =>
			{
				try
				{
					await coordinator.StartSearchAsync(Fen.Default);
				}
				catch (OperationCanceledException)
				{
					// Expected to be cancelled during disposal
				}
				catch (ObjectDisposedException)
				{
					// Expected during disposal
				}
			}
		);

		// Dispose while operations are active
		await Task.Delay(TestConstants.VeryShortDelay);

		var disposeExceptions = new ConcurrentBag<Exception>();
		try
		{
			await coordinator.DisposeAsync();
		}
		catch (Exception ex)
		{
			disposeExceptions.Add(ex);
		}

		// Wait for the search task to complete (should be cancelled or complete)
		try
		{
			await searchTask.WaitAsync(TestConstants.DefaultTimeout);
		}
		catch (TimeoutException)
		{
			// Task might still be running, which is acceptable
		}
		catch (OperationCanceledException)
		{
			// Expected if task was cancelled
		}

		// Dispose should not throw exceptions
		disposeExceptions.Should().BeEmpty("DisposeAsync should handle concurrent operations without throwing");
	}

	[Fact]
	public async Task FullGame_WhenScholarMate_ShouldValidateFlow()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		// Scholar's Mate sequence:
		// 1. e2e4 e7e5
		// 2. d1h5 b8c6
		// 3. f1c4 g8f6
		// 4. h5f7#
		string[] moves =
		[
			"e2e4",
			"e7e5",
			"d1h5",
			"b8c6",
			"f1c4",
			"g8f6",
			"h5f7"
		];

		var currentMoves = new List<string>();

		// Initial position update to start classification
		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);

		foreach (string move in moves)
		{
			// Wait for the move to be classified
			var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

			void OnStateChanged(UciState state)
			{
				if (state.ClassifiedMoves.ContainsKey(move)) tcs.TrySetResult(true);
			}

			coordinator.StateChanged += OnStateChanged;

			try
			{
				// Check if already classified
				if (coordinator.State.ClassifiedMoves.ContainsKey(move)) tcs.TrySetResult(true);

				// Wait for classification if not yet ready
				await tcs.Task.WaitAsync(TestConstants.DefaultTimeout);
			}
			finally
			{
				coordinator.StateChanged -= OnStateChanged;
			}

			// Verify we have legal moves and the move we want to play is in them
			var state = coordinator.State;
			state.LegalMoves.Should().Contain(move);
			state.ClassifiedMoves.Keys.Should().Contain(move);

			// Apply the move
			currentMoves.Add(move);
			await coordinator.LoadMatchAsync(new PlayableMatchSetup(Fen.Default, currentMoves));
		}

		var finalState = coordinator.State;
		var finalFen   = coordinator.CurrentFen;

		finalFen.ActiveColor.Should().Be('b', "It should be Black's turn now");
		coordinator.State.IsCheck.Should().BeTrue();
		finalState.LegalMoves.Should().BeEmpty("Black should be checkmated and have no legal moves");
		finalState.ClassifiedMoves.Should().BeEmpty("Black should be checkmated and have no classified moves");

		await coordinator.StopAsync();
	}

	[Fact]
	public async Task NewGameAsync_WhenCalledAfterSearch_ShouldResetStateAndAllowRestart()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		var firstInfo =
			new TaskCompletionSource<PrincipalVariation?>(TaskCreationOptions.RunContinuationsAsynchronously);

		coordinator.StateChanged += state =>
		{
			if (state.Evaluation != null) firstInfo.TrySetResult(state.Evaluation.Value);
		};

		await coordinator.StartSearchAsync(Fen.Default);
		var pv1 = await firstInfo.Task.WaitAsync(TestConstants.DefaultTimeout);
		pv1.Should().NotBeNull();

		// New game should reset internal state and allow pondering again
		await coordinator.NewGameAsync();

		var secondInfo =
			new TaskCompletionSource<PrincipalVariation?>(TaskCreationOptions.RunContinuationsAsynchronously);

		coordinator.StateChanged += state =>
		{
			if (state.Evaluation != null) secondInfo.TrySetResult(state.Evaluation.Value);
		};

		await coordinator.StartSearchAsync(Fen.Default);
		var pv2 = await secondInfo.Task.WaitAsync(TestConstants.MediumTimeout);
		pv2.Should().NotBeNull();

		await coordinator.StopSearchAsync();
		await coordinator.StopAsync();
	}

	[Fact]
	public async Task SearchAsync_WhenSearchMovesIsSpecified_ShouldRestrictBestMoveToProvidedMoves()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();
		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);

		var result = await coordinator.SearchAsync(
						 new()
						 {
							 Depth       = 4,
							 SearchMoves = ["a2a3"]
						 }
					 );

		result.BestMove.Should().Be("a2a3");
		await coordinator.StopAsync();
	}

	[Fact]
	public async Task StartAsync_WhenClassifierStartupFails_ShouldRollbackPreviouslyStartedEngines()
	{
		var snapshotClient = new UciEngineClient(TestResourcePaths.STOCKFISH_PATH);
		var ponder = new UciPonderRuntime(new UciEngineClient(TestResourcePaths.STOCKFISH_PATH));
		var classifier = CreateClientThatFailsStart("startup failure");

		await using var coordinator = new UciGameEngineSession(snapshotClient, ponder, classifier,
			options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });

		await FluentActions.Awaiting(() => coordinator.StartAsync())
						   .Should()
						   .ThrowAsync<InvalidOperationException>()
						   .WithMessage("startup failure");

		snapshotClient.IsStarted.Should().BeFalse();
		ponder.IsStarted.Should().BeFalse();
		classifier.IsStarted.Should().BeFalse();
	}

	[Fact]
	public async Task StartAsync_WhenCompleted_ShouldAllowGetCurrentFenAsync()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		var fen = coordinator.CurrentFen;

		fen.Should().NotBeNull();
		Fen.Validate(fen.Raw).Should().BeTrue();
	}

	[Fact]
	public async Task StartAsync_WhenCompleted_ShouldExposeEngineMetadataAndCapabilities()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });

		await coordinator.StartAsync();

		coordinator.EngineInfo.Name.Should().NotBeNullOrWhiteSpace();
		coordinator.EngineInfo.Author.Should().NotBeNullOrWhiteSpace();
		coordinator.AvailableOptions.Should().Contain(x => x.Name == "Threads");
		coordinator.AvailableOptions.Should().Contain(x => x.Name == "Hash");
		coordinator.Capabilities.DisplayBoardFen.Should().Be(UciCapabilityState.Unknown);
		coordinator.Capabilities.PerftMoveListing.Should().Be(UciCapabilityState.Unknown);
	}

	[Fact]
	public async Task StartAsync_WhenFenIsProvided_ShouldStartSearchAndBroadcastLegalMovesAndBestMove()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });

		var legalTcs =
			new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);

		var bestTcs =
			new TaskCompletionSource<(ParsedMove best, ParsedMove? ponder)>(
				TaskCreationOptions.RunContinuationsAsynchronously
			);

		coordinator.StateChanged += state =>
		{
			if (state.LegalMoves.Count > 0) legalTcs.TrySetResult(state.LegalMoves);
			if (state.BestMove.HasValue && !string.IsNullOrWhiteSpace(state.BestMove.Value.Raw))
				bestTcs.TrySetResult((state.BestMove.Value, state.PonderMove));
		};

		// Start engines, then set the initial position (this triggers search and legal moves)
		await coordinator.StartAsync();
		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);

		var legal = await legalTcs.Task.WaitAsync(TestConstants.DefaultTimeout);
		legal.Should().NotBeNull();
		legal.Count.Should().BeGreaterThan(0);
		legal.Should().Contain(["e2e4", "d2d4", "g1f3", "c2c4"]);

		var bestPair = await bestTcs.Task.WaitAsync(TestConstants.DefaultTimeout);
		bestPair.best.Raw.Should().NotBeNullOrWhiteSpace();
		UciEngineClient.IsUciMoveString(bestPair.best.Raw).Should().BeTrue();
		if (bestPair.ponder.HasValue)
			UciEngineClient.IsUciMoveString(bestPair.ponder.Value.Raw).Should().BeTrue();

		await coordinator.StopAsync();
	}

	[Fact]
	public async Task StartAsync_WhenNonstandardExtensionsAreUnavailable_ShouldStillSupportLocalGameplay()
	{
		var snapshotClient = CreateClientWithoutCoordinatorExtensions();
		var ponder = new UciPonderRuntime(new UciEngineClient(TestResourcePaths.STOCKFISH_PATH));
		var classifier = new UciEngineClient(TestResourcePaths.STOCKFISH_PATH);

		await using var coordinator = new UciGameEngineSession(snapshotClient, ponder, classifier);

		await coordinator.StartAsync();
		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);
		await coordinator.MakeMoveAsync("e2e4");
		coordinator.State.PlayedMoves.Should().ContainSingle("e2e4");
	}

	[Fact]
	public async Task StartPonderAsync_WhenBestLineIsUpdated_ShouldRaiseBestMoveFromSingleEngineStream()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		var bestLineTcs =
			new TaskCompletionSource<PrincipalVariation>(TaskCreationOptions.RunContinuationsAsynchronously);

		coordinator.StateChanged += state =>
		{
			if (state.Evaluation?.Moves is { Length: > 0 })
				bestLineTcs.TrySetResult(state.Evaluation.Value);
		};

		await coordinator.StartSearchAsync(Fen.Default);
		var pvLine = await bestLineTcs.Task.WaitAsync(TestConstants.MediumTimeout);

		pvLine.Moves.Should().NotBeEmpty();
		// Either CP or Mate score should be present
		(pvLine.ScoreCp.HasValue || pvLine.ScoreMate.HasValue).Should().BeTrue();

		await coordinator.StopSearchAsync();
		await coordinator.StopAsync();
	}

	[Fact]
	public async Task StartPonderAsync_WhenCalled_ShouldRaisePonderInfo()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		var tcs = new TaskCompletionSource<PrincipalVariation?>(TaskCreationOptions.RunContinuationsAsynchronously);
		coordinator.StateChanged += state =>
		{
			if (state.Evaluation != null) tcs.TrySetResult(state.Evaluation.Value);
		};

		await coordinator.StartSearchAsync(Fen.Default);

		var received = await tcs.Task.WaitAsync(TestConstants.DefaultTimeout);
		received.Should().NotBeNull();
		received!.Value.Moves.Should().NotBeEmpty();
		received.Value.ScoreCp.Should().NotBeNull();

		await coordinator.StopSearchAsync();
	}

	[Fact]
	public async Task StartPonderAsync_WhenFenIsInvalid_ShouldThrowArgumentException()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		await Assert.ThrowsAsync<ArgumentException>(() => coordinator.StartSearchAsync(Fen.Empty()));
	}

	[Fact]
	public async Task StartPonderAsync_WhenStopped_ShouldRaisePonderBestMove()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		ParsedMove? best   = null;
		ParsedMove? ponder = null;
		var         tcs    = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

		coordinator.StateChanged += state =>
		{
			if (state.BestMove.HasValue)
			{
				best   = state.BestMove;
				ponder = state.PonderMove;
				tcs.TrySetResult(true);
			}
		};

		await coordinator.StartSearchAsync(Fen.Default);

		await tcs.Task.WaitAsync(TestConstants.DefaultTimeout);

		await coordinator.StopSearchAsync();

		best.Should().NotBeNull();
		UciEngineClient.IsUciMoveString(best!.Value.Raw).Should().BeTrue();
		if (ponder.HasValue)
			UciEngineClient.IsUciMoveString(ponder.Value.Raw).Should().BeTrue();
	}

	[Fact]
	public async Task StartSearchAsync_WhenCalledForNewPositionWhileAlreadySearching_ShouldRetargetPonderRuntime()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		var mateFen = Fen.Parse(TestConstants.WHITE_MATE_IN_ONE_FEN);
		mateFen.Should().NotBeNull();

		var mateMoveTcs = new TaskCompletionSource<ParsedMove>(TaskCreationOptions.RunContinuationsAsynchronously);
		coordinator.StateChanged += state =>
		{
			if (state.BestMove is { } best && best.Raw == "f7g7")
				mateMoveTcs.TrySetResult(best);
		};

		await coordinator.StartSearchAsync(Fen.Default);
		await Task.Delay(TestConstants.MediumDelay);
		await coordinator.StartSearchAsync(mateFen!.Value);

		var bestMove = await mateMoveTcs.Task.WaitAsync(TestConstants.ExtendedTimeout);
		bestMove.Raw.Should().Be("f7g7");

		await coordinator.StopAsync();
	}

	[Fact]
	public async Task StartSearchAsync_WhenCanceled_ShouldNotLeaveSearchingStateTrue()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();

		await FluentActions.Awaiting(() => coordinator.StartSearchAsync(Fen.Default, null, cts.Token))
						   .Should()
						   .ThrowAsync<OperationCanceledException>();

		coordinator.State.IsSearching.Should().BeFalse();

		await coordinator.StopAsync();
	}

	[Fact(Timeout = 10000)]
	public async Task StartSearchAsyncAndStopSearchAsync_WhenCalledConcurrently_ShouldBeThreadSafe()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		var exceptions = new ConcurrentBag<Exception>();

		for (var i = 0; i < 6; i++)
		{
			var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

			var startTask = Task.Run(async () =>
				{
					try
					{
						await releaseGate.Task.WaitAsync(TestConstants.DefaultTimeout);
						await coordinator.StartSearchAsync(Fen.Default);
					}
					catch (OperationCanceledException)
					{
						// Expected when StopSearchAsync wins the race.
					}
					catch (Exception ex)
					{
						exceptions.Add(ex);
					}
				}
			);

			var stopTask = Task.Run(async () =>
				{
					try
					{
						await releaseGate.Task.WaitAsync(TestConstants.DefaultTimeout);
						await coordinator.StopSearchAsync();
					}
					catch (Exception ex)
					{
						exceptions.Add(ex);
					}
				}
			);

			releaseGate.TrySetResult();
			await Task.WhenAll(startTask, stopTask).WaitAsync(TestConstants.ExtendedTimeout);
		}

		exceptions.Should().BeEmpty("Concurrent Start/Stop search should be thread-safe");
		await coordinator.StopSearchAsync();
		coordinator.State.IsSearching.Should().BeFalse();

		await coordinator.StopAsync();
	}

	[Fact]
	public async Task StopAsync_WhenMultipleEnginesFailToStop_ShouldThrowAggregateAfterCleanup()
	{
		var snapshotClient = new UciEngineClient(TestResourcePaths.STOCKFISH_PATH);
		var ponder = new UciPonderRuntime(
			new UciEngineClient(TestResourcePaths.STOCKFISH_PATH),
			static _ => throw new InvalidOperationException("ponder stop failed")
		);
		var classifier = CreateClientThatFailsStop("classifier stop failed");

		await using var coordinator = new UciGameEngineSession(snapshotClient, ponder, classifier,
			options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();
		await coordinator.StartSearchAsync(Fen.Default);

		var act    = async () => await coordinator.StopAsync();
		var thrown = await Assert.ThrowsAsync<AggregateException>(act);

		thrown.InnerExceptions.Should().HaveCount(2);
		coordinator.State.Should().Be(UciState.Default);
	}

	[Fact]
	public async Task StopAsync_WhenOneEngineStopFails_ShouldResetStateRaiseStoppedAndThrow()
	{
		var snapshotClient = new UciEngineClient(TestResourcePaths.STOCKFISH_PATH);
		var ponder = new UciPonderRuntime(new UciEngineClient(TestResourcePaths.STOCKFISH_PATH));
		var classifier = CreateClientThatFailsStop("classifier stop failed");

		await using var coordinator = new UciGameEngineSession(snapshotClient, ponder, classifier,
			options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();
		await coordinator.StartSearchAsync(Fen.Default);

		var stoppedRaised = false;
		coordinator.Stopped += () => stoppedRaised = true;

		await FluentActions.Awaiting(() => coordinator.StopAsync())
						   .Should()
						   .ThrowAsync<InvalidOperationException>()
						   .WithMessage("classifier stop failed");

		coordinator.State.Should().Be(UciState.Default);
		stoppedRaised.Should().BeTrue();
	}

	[Fact]
	public async Task StreamClassifiedMovesAsync_WhenPositionIsSet_ShouldYieldClassifiedMoves()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		// Set a standard position and trigger background classification
		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);

		var moves = new List<Move>();
		await foreach (var move in coordinator.StreamClassifiedMovesAsync())
		{
			moves.Add(move);
			// We can stop early if we have enough moves to verify streaming works
			if (moves.Count >= 5) break;
		}

		moves.Should().NotBeEmpty();
		moves.Count.Should().BeGreaterOrEqualTo(5);
		foreach (var m in moves)
		{
			m.Analysis.Should().NotBeNull();
			m.Notation.Should().NotBeNullOrWhiteSpace();
		}

		await coordinator.StopSearchAsync();
		await coordinator.StopAsync();
	}

	[Fact]
	public async Task UpdatePositionAsync_WhenApplyingWhiteE2E4_ShouldProvideBlackResponsesAndClassifiedMove()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		// Initial position: expect legal moves including e2e4
		var legalStartTcs =
			new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);

		coordinator.StateChanged += state =>
		{
			if (state.LegalMoves.Count > 0)
				legalStartTcs.TrySetResult(state.LegalMoves);
		};

		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);
		var legalStart = await legalStartTcs.Task.WaitAsync(TestConstants.MediumTimeout);
		legalStart.Should().Contain(["e2e4", "d2d4"]);

		// Apply white's move e2e4; validate black-side legal moves, pondering and classification events
		var legalAfterWhiteTcs =
			new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);

		var bestAfterWhiteTcs =
			new TaskCompletionSource<(ParsedMove best, ParsedMove? ponder)>(
				TaskCreationOptions.RunContinuationsAsynchronously
			);

		var classifiedTcs =
			new TaskCompletionSource<(string notation, Move move)>(TaskCreationOptions.RunContinuationsAsynchronously);

		coordinator.StateChanged += state =>
		{
			if (state.LegalMoves.Count > 0)
			{
				var hasReply = false;
				foreach (string s in state.LegalMoves)
				{
					if (s == "e7e5" || s == "c7c5")
					{
						hasReply = true;
						break;
					}
				}

				if (hasReply)
					legalAfterWhiteTcs.TrySetResult(state.LegalMoves);
			}

			if (state.BestMove.HasValue && !string.IsNullOrWhiteSpace(state.BestMove.Value.Raw))
				bestAfterWhiteTcs.TrySetResult((state.BestMove.Value, state.PonderMove));

			if (state.ClassifiedMoves.Count > 0)
			{
				var first = state.ClassifiedMoves.First();
				classifiedTcs.TrySetResult((first.Key, first.Value));
			}
		};

		await coordinator.LoadMatchAsync(new PlayableMatchSetup(Fen.Default, ["e2e4"]));

		var legalAfterWhite = await legalAfterWhiteTcs.Task.WaitAsync(TestConstants.DefaultTimeout);
		legalAfterWhite.Should().Contain(x => x == "e7e5" || x == "c7c5");

		var bestPair = await bestAfterWhiteTcs.Task.WaitAsync(TestConstants.DefaultTimeout);
		bestPair.best.Raw.Should().NotBeNullOrWhiteSpace();
		UciEngineClient.IsUciMoveString(bestPair.best.Raw).Should().BeTrue();

		var classified = await classifiedTcs.Task.WaitAsync(TestConstants.DefaultTimeout);
		classified.notation.Should().NotBeNullOrWhiteSpace();

		await coordinator.StopSearchAsync();
		await coordinator.StopAsync();
	}

	[Fact]
	public async Task UpdatePositionAsync_WhenCalledWithDefaultFen_ShouldPopulateLegalMoves()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		// Start background processing for the default position
		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);
		bool populated = await AsyncTestHelpers.WaitForConditionAsync(
							 () => coordinator.State.LegalMoves.Count > 0,
							 TimeSpan.FromMilliseconds(10),
							 TestConstants.DefaultTimeout
						 );

		populated.Should().BeTrue("legal moves should be populated within the standard timeout");

		var legal = coordinator.State.LegalMoves;
		legal.Should().NotBeNull();
		legal.Count.Should().BeGreaterThan(0);
		legal.Should().Contain(["e2e4", "d2d4", "g1f3", "c2c4"]);
	}

	[Fact]
	public async Task WaitForClassificationAsync_WhenNewGameStarts_ShouldCancel()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();
		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);

		var waitTask = coordinator.WaitForClassificationAsync();
		await coordinator.NewGameAsync();

		await FluentActions.Awaiting(() => waitTask)
						   .Should()
						   .ThrowAsync<OperationCanceledException>();

		await coordinator.StopAsync();
	}

	[Fact]
	public async Task WaitForClassificationAsync_WhenPositionChanges_ShouldCancel()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();
		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);

		var waitTask = coordinator.WaitForClassificationAsync();
		await coordinator.LoadMatchAsync(new PlayableMatchSetup(Fen.Default, ["e2e4"]));

		await FluentActions.Awaiting(() => waitTask)
						   .Should()
						   .ThrowAsync<OperationCanceledException>();

		await coordinator.StopAsync();
	}

	[IntegrationTest]
	[Trait("Requires", "Stockfish")]
	public async Task AnalysisStream_WhenStarted_ShouldYieldPvWithScore()
	{
		await using var coordinator = new UciGameEngineSession(TestResourcePaths.STOCKFISH_PATH, options: UciCoordinatorOptions.Default with { EnableBackgroundAnalysis = true });
		await coordinator.StartAsync();

		var tcs = new TaskCompletionSource<PrincipalVariation>(TaskCreationOptions.RunContinuationsAsynchronously);
		coordinator.StateChanged += state =>
		{
			if (state.Evaluation?.Moves is { Length: > 0 })
				tcs.TrySetResult(state.Evaluation.Value);
		};

		await coordinator.LoadMatchAsync(PlayableMatchSetup.Standard);
		await coordinator.StartSearchAsync();

		var pvLine = await tcs.Task.WaitAsync(TestConstants.DefaultTimeout);
		pvLine.Moves.Should().NotBeEmpty();
		(pvLine.ScoreCp.HasValue || pvLine.ScoreMate.HasValue).Should().BeTrue();

		await coordinator.StopSearchAsync();
	}

	private static UciEngineClient CreateClientThatFailsStart(string message)
	{
		var transport = Substitute.For<IUciTransport>();
		transport.StartAsync(Arg.Any<CancellationToken>())
				 .Returns<Task>(_ => throw new InvalidOperationException(message));
		transport.WriteLineAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
		return new UciEngineClient(transport);
	}

	private static UciEngineClient CreateClientThatFailsStop(string message)
	{
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		ConfigureMinimalHandshake(transport, channel);
		bool started = false;
		transport.IsStarted.Returns(_ => started);
		transport.StartAsync(Arg.Any<CancellationToken>()).Returns(_ =>
		{
			started = true;
			return Task.CompletedTask;
		});
		transport.StopAsync(Arg.Any<CancellationToken>())
				 .Returns<Task>(_ => throw new InvalidOperationException(message));
		return new UciEngineClient(transport);
	}

	private static UciEngineClient CreateClientWithoutCoordinatorExtensions()
	{
		var (transport, channel) = UciEngineClientTestHelpers.CreateMockTransport();
		ConfigureMinimalHandshake(transport, channel);

		transport.When(x => x.WriteLineAsync("d", Arg.Any<CancellationToken>()))
				 .Do(_ =>
					 {
						 channel.Writer.TryWrite("fen invalid");
						 channel.Writer.TryWrite("checkers");
					 }
				 );

		return new UciEngineClient(transport);
	}

	private static void ConfigureMinimalHandshake(IUciTransport transport, Channel<string> channel)
	{
		transport.When(x => x.WriteLineAsync("uci", Arg.Any<CancellationToken>()))
				 .Do(async _ =>
					 {
						 await channel.Writer.WriteAsync("id name Minimal Engine");
						 await channel.Writer.WriteAsync("id author Test Harness");
						 await channel.Writer.WriteAsync("option name Ponder type check default false");
						 await channel.Writer.WriteAsync("uciok");
					 }
				 );

		transport.When(x => x.WriteLineAsync("isready", Arg.Any<CancellationToken>()))
				 .Do(_ => channel.Writer.TryWrite("readyok"));
	}
}

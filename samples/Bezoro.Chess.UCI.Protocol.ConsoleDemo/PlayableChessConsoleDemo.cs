using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.UCI.Protocol.ConsoleDemo;

internal static class PlayableChessConsoleDemo
{
	private const int ADVANTAGE_BAR_HEIGHT       = 8;
	private const int ADVANTAGE_BAR_WIDTH        = 3;
	private const int ENGINE_MOVE_TIME_MS        = 1_000;
	private const int ANALYSIS_MOVE_TIME_MS = 3_000;
	private const int ANALYSIS_FALLBACK_TIME_MS = 250;

	public static async Task<int> RunAsync(string[] args)
	{
		Console.Title = "Bezoro Chess UCI Protocol Console Demo";
		Console.WriteLine("Bezoro Chess UCI Protocol Console Demo");
		Console.WriteLine("--------------------------------------");
		Console.WriteLine("Play against a UCI engine using UCI move notation such as e2e4 or a7a8q.");
		Console.WriteLine(
			"Type 'moves' to list legal moves, 'history' to show move eval history, 'undo' to rewind moves, 'loadfen <fen>' to jump to a position, or 'quit' to exit."
		);

		Console.WriteLine("Debug FENs to paste:");
		foreach (var example in DebugLoadFenExampleCatalog.Examples)
			Console.WriteLine($"  {example.Label,-11} {example.Command}");

		Console.WriteLine();

		string enginePath = PromptEnginePath(args);

		var options = new UciClientOptions
		{
			UciHandshakeTimeout             = TimeSpan.FromSeconds(5),
			ReadyTimeout                    = TimeSpan.FromSeconds(10),
			DisplayBoardFenTimeout          = TimeSpan.FromSeconds(3),
			DisplayBoardCheckersGracePeriod = TimeSpan.FromSeconds(1),
			DefaultSearchTimeout            = TimeSpan.FromSeconds(20),
			MoveTimeBuffer                  = TimeSpan.FromSeconds(1)
		};

		await using var playingClient  = new UciEngineClient(enginePath, options: options);
		await using var analysisClient = new UciEngineClient(enginePath, options: options);

		playingClient.StderrReceived  += static line => Console.Error.WriteLine($"play stderr: {line}");
		analysisClient.StderrReceived += static line => Console.Error.WriteLine($"analysis stderr: {line}");

		try
		{
			await playingClient.StartAsync(CancellationToken.None);
			await analysisClient.StartAsync(CancellationToken.None);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Failed to start engine: {ex.Message}");
			return 1;
		}

		Console.WriteLine($"{playingClient.EngineInfo.Name} by {playingClient.EngineInfo.Author}");
		Console.WriteLine($"Engine executable: {enginePath}");
		Console.WriteLine();

		if (playingClient.TryGetStrengthLimitRange(out int minElo, out int maxElo))
		{
			int elo = PromptElo(minElo, maxElo);
			await playingClient.SetStrengthLimitAsync(elo, CancellationToken.None);
			Console.WriteLine($"Engine strength limited to {elo} Elo.");
			Console.WriteLine("Analysis remains at full engine strength.");
		}
		else
		{
			Console.WriteLine("Engine does not advertise adjustable Elo strength. Using its default strength.");
		}

		char playerColor = PromptPlayerColor();
		Console.WriteLine($"You are playing {(playerColor == 'w' ? "White" : "Black")}.");
		Console.WriteLine($"Engine move time: {ENGINE_MOVE_TIME_MS} ms per move.");
		Console.WriteLine();

		try
		{
			await using var session = new UciPlayableMatchSession(
				playingClient,
				analysisClient,
				playerColor,
				engineMoveTimeMs: ENGINE_MOVE_TIME_MS,
				analysisMoveTimeMs: ANALYSIS_MOVE_TIME_MS,
				analysisFallbackMoveTimeMs: ANALYSIS_FALLBACK_TIME_MS
			);

			await RunGameLoopAsync(session);
			return 0;
		}
		catch (NotSupportedException ex)
		{
			Console.Error.WriteLine(ex.Message);
			return 1;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Game aborted due to an unexpected error: {ex.Message}");
			return 1;
		}
	}

	private static (int topRow, int width, int height) WriteFreshFrame(string[] frame)
	{
		int width = frame.Select(static line => line.Length).DefaultIfEmpty().Max();

		for (var i = 0; i < frame.Length; i++)
		{
			if (i == frame.Length - 1)
				Console.Write(frame[i]);
			else
				Console.WriteLine(frame[i].PadRight(width));
		}

		int topRow = ConsolePromptLayout.GetTopRowFromBottomRow(Console.CursorTop, Console.BufferHeight, frame.Length);
		return (topRow, width, frame.Length);
	}

	private static (int width, int height) RenderFrame(
		int      topRow,
		string[] frame,
		string   currentInput,
		int      previousWidth  = 0,
		int      previousHeight = 0)
	{
		const string promptPrefix = "Your move: ";
		int width = Math.Max(
			previousWidth,
			frame.Select(static line => line.Length).DefaultIfEmpty().Max() + currentInput.Length
		);

		int height = Math.Max(previousHeight, frame.Length);
		topRow = ConsolePromptLayout.GetSafeTopRow(topRow, Console.BufferHeight, height);

		Console.SetCursorPosition(0, topRow);

		for (var i = 0; i < height; i++)
		{
			string line = i < frame.Length ? frame[i] : string.Empty;
			if (i == frame.Length - 1 && i < frame.Length)
				line += currentInput;

			Console.Write(line.PadRight(width));
			if (i < height - 1)
				Console.WriteLine();
		}

		Console.SetCursorPosition(promptPrefix.Length + currentInput.Length, topRow + frame.Length - 1);
		return (width, height);
	}

	private static char PromptPlayerColor()
	{
		while (true)
		{
			Console.Write("Play as white or black? [w/b]: ");
			string input = NormalizeInput(ReadRequiredLine()).ToLowerInvariant();

			if (input is "w" or "white") return 'w';
			if (input is "b" or "black") return 'b';

			Console.WriteLine("Please enter 'w', 'white', 'b', or 'black'.");
		}
	}

	private static int PromptElo(int minElo, int maxElo)
	{
		while (true)
		{
			Console.Write($"Engine Elo ({minElo}-{maxElo}): ");
			string input = ReadRequiredLine();

			if (!int.TryParse(input, out int elo))
			{
				Console.WriteLine("Please enter a whole number.");
				continue;
			}

			if (elo < minElo || elo > maxElo)
			{
				Console.WriteLine($"Please choose an Elo between {minElo} and {maxElo}.");
				continue;
			}

			return elo;
		}
	}

	private static string NormalizeInput(string value)
	{
		string trimmed = value.Trim();
		return trimmed.Trim('"');
	}

	private static string PromptEnginePath(string[] args)
	{
		string? candidate = args.Length > 0 ? NormalizeInput(args[0]) : null;

		while (true)
		{
			if (!string.IsNullOrWhiteSpace(candidate))
			{
				string fullPath = Path.GetFullPath(candidate);
				if (File.Exists(fullPath)) return fullPath;

				Console.WriteLine($"Engine executable not found: {fullPath}");
			}

			Console.Write("Engine path: ");
			candidate = NormalizeInput(ReadRequiredLine());
		}
	}

	private static string ReadRequiredLine()
	{
		string? line = Console.ReadLine();
		return line?.Trim() ?? string.Empty;
	}

	private static string[] BuildBoardLines(
		Fen               fen,
		char              playerColor,
		int               legalMoveCount,
		PositionAdvantage advantage)
	{
		string[] boardLines     = fen.ToDisplayLines(playerColor, legalMoveCount);
		string[] advantageLines = advantage.ToDisplayBarLines();
		int      lineCount      = Math.Max(boardLines.Length, advantageLines.Length);
		int      boardWidth     = boardLines.Length == 0 ? 0 : boardLines.Max(static line => line.Length);
		var      renderedLines  = new string[lineCount + 1];

		renderedLines[0] = string.Empty;
		for (var i = 0; i < lineCount; i++)
		{
			string boardLine     = i < boardLines.Length ? boardLines[i] : string.Empty;
			string advantageLine = i < advantageLines.Length ? advantageLines[i] : string.Empty;
			renderedLines[i + 1] = $"{boardLine.PadRight(boardWidth)}   {advantageLine}";
		}

		return renderedLines;
	}

	private static string[] BuildPromptFrame(
		Fen               fen,
		char              playerColor,
		int               legalMoveCount,
		PositionAdvantage advantage)
	{
		string[] boardLines = BuildBoardLines(fen, playerColor, legalMoveCount, advantage);
		return [.. boardLines, "Your move: "];
	}

	private static async Task PrintLegalMovesAsync(UciPlayableMatchSession session)
	{
		await session.WaitForCurrentMoveClassificationsAsync();
		var analysis = await session.GetLegalMoveAnalysisAsync();
		if (analysis.Evaluations.IsDefaultOrEmpty)
		{
			Console.WriteLine("No legal moves are available.");
			return;
		}

		Console.WriteLine("Showing cached legal move analysis...");

		foreach (var evaluation in analysis.Evaluations)
			Console.WriteLine($"  {evaluation.ToDebugDisplayString()}");
	}

	private static async Task RunGameLoopAsync(
		UciPlayableMatchSession session)
	{
		await session.StartNewGameAsync(CancellationToken.None);

		while (true)
		{
			var state      = await session.RefreshAsync(CancellationToken.None);
			var fen        = state.Fen;
			var legalMoves = state.LegalMoves;

			if (legalMoves.Length == 0)
			{
				PrintBoard(
					fen,
					session.PlayerColor,
					legalMoves.Length,
					session.ResolveCurrentAdvantage()
				);

				session.CancelAnalysis();
				PrintGameOver(fen, session.PlayerColor);
				return;
			}

			if (fen.ActiveColor == session.PlayerColor)
			{
				var command = await PromptHumanMoveAsync(
								  session,
								  state
							  );

				if (command.Kind == PlayableMatchCommandKind.Quit)
				{
					session.CancelAnalysis();
					Console.WriteLine("Game aborted by user.");
					return;
				}

				if (command.Kind == PlayableMatchCommandKind.LoadFen)
				{
					await session.LoadMatchAsync(new(command.Fen!.Value, command.Moves), CancellationToken.None);
					Console.WriteLine($"Loaded FEN: {command.Fen.Value.Raw}");
					continue;
				}

				if (command.Kind == PlayableMatchCommandKind.Undo)
				{
					UndoMostRecentTurn(session, state);
					continue;
				}

				session.ApplyMove(command.Move!);
				continue;
			}

			PrintBoard(
				fen,
				session.PlayerColor,
				legalMoves.Length,
				session.ResolveCurrentAdvantage()
			);

			Console.WriteLine("Engine is thinking...");
			var engineMove = await session.PlayControlledMoveAsync(CancellationToken.None);

			Console.WriteLine(
				$"Engine plays {engineMove.Move}{engineMove.SearchResult.ToPlayerDisplayString(session.EngineColor, session.PlayerColor)}"
			);
		}
	}

	private static async Task<PlayableMatchCommand> PromptHumanMoveAsync(
		UciPlayableMatchSession session,
		PlayableMatchState      state)
	{
		if (Console.IsInputRedirected || Console.IsOutputRedirected)
			return await PromptHumanMoveRedirectedAsync(
					   session,
					   state
				   );

		return await PromptHumanMoveInteractiveAsync(
				   session,
				   state
			   );
	}

	private static async Task<PlayableMatchCommand> PromptHumanMoveInteractiveAsync(
		UciPlayableMatchSession session,
		PlayableMatchState      state)
	{
		while (true)
		{
			var command = PlayableMatchCommandParser.Parse(
				NormalizeInput(
					await ReadInteractiveInputAsync(
						session.ResolveCurrentAdvantage,
						advantage => BuildPromptFrame(
							state.Fen, session.PlayerColor, state.LegalMoves.Length, advantage
						)
					)
				)
			);

			if (command.Kind == PlayableMatchCommandKind.Quit)
				return command;

			if (PlayableTurnCommandRouter.ShouldHandleInsidePrompt(command.Kind) &&
				command.Kind == PlayableMatchCommandKind.Moves)
			{
				await PrintLegalMovesAsync(session);
				continue;
			}

			if (PlayableTurnCommandRouter.ShouldHandleInsidePrompt(command.Kind) &&
				command.Kind == PlayableMatchCommandKind.History)
			{
				PrintMoveHistory(session);
				continue;
			}

			if (command.Kind == PlayableMatchCommandKind.LoadFen)
				return command;

			if (PlayableTurnCommandRouter.ShouldValidateAsMove(command.Kind) &&
				command.Kind == PlayableMatchCommandKind.Invalid)
			{
				Console.WriteLine(command.Error);
				continue;
			}

			if (!PlayableTurnCommandRouter.ShouldValidateAsMove(command.Kind))
				return command;

			if (!state.LegalMoves.ContainsUciMove(command.Move!))
			{
				Console.WriteLine("That move is not legal in the current position.");
				await PrintLegalMovesAsync(session);
				continue;
			}

			return command;
		}
	}

	private static async Task<PlayableMatchCommand> PromptHumanMoveRedirectedAsync(
		UciPlayableMatchSession session,
		PlayableMatchState      state)
	{
		while (true)
		{
			PrintBoard(
				state.Fen,
				session.PlayerColor,
				state.LegalMoves.Length,
				session.ResolveCurrentAdvantage()
			);

			Console.Write("Your move: ");
			var command = PlayableMatchCommandParser.Parse(NormalizeInput(ReadRequiredLine()));

			if (command.Kind == PlayableMatchCommandKind.Quit)
				return command;

			if (PlayableTurnCommandRouter.ShouldHandleInsidePrompt(command.Kind) &&
				command.Kind == PlayableMatchCommandKind.Moves)
			{
				await PrintLegalMovesAsync(session);
				continue;
			}

			if (PlayableTurnCommandRouter.ShouldHandleInsidePrompt(command.Kind) &&
				command.Kind == PlayableMatchCommandKind.History)
			{
				PrintMoveHistory(session);
				continue;
			}

			if (command.Kind == PlayableMatchCommandKind.LoadFen)
				return command;

			if (PlayableTurnCommandRouter.ShouldValidateAsMove(command.Kind) &&
				command.Kind == PlayableMatchCommandKind.Invalid)
			{
				Console.WriteLine(command.Error);
				continue;
			}

			if (!PlayableTurnCommandRouter.ShouldValidateAsMove(command.Kind))
				return command;

			if (!state.LegalMoves.ContainsUciMove(command.Move!))
			{
				Console.WriteLine("That move is not legal in the current position.");
				await PrintLegalMovesAsync(session);
				continue;
			}

			return command;
		}
	}

	private static async Task<string> ReadInteractiveInputAsync(
		Func<PositionAdvantage>           getAdvantage,
		Func<PositionAdvantage, string[]> buildFrame)
	{
		string   currentInput  = string.Empty;
		var      lastAdvantage = getAdvantage();
		string[] frame         = buildFrame(lastAdvantage);

		if (!ConsolePromptLayout.CanRenderInPlace(frame.Length, Console.BufferHeight))
		{
			foreach (string line in frame[..^1])
				Console.WriteLine(line);

			Console.Write(frame[^1]);
			return ReadRequiredLine();
		}

		var frameSize = WriteFreshFrame(frame);
		int topRow    = frameSize.topRow;

		while (true)
		{
			while (Console.KeyAvailable)
			{
				var key = Console.ReadKey(true);

				switch (key.Key)
				{
					case ConsoleKey.Enter:
						Console.WriteLine();
						return currentInput;
					case ConsoleKey.Backspace when currentInput.Length > 0:
						currentInput = currentInput[..^1];
						var backspaceFrame = RenderFrame(
							topRow, frame, currentInput, frameSize.width, frameSize.height
						);

						frameSize = (topRow, backspaceFrame.width, backspaceFrame.height);
						break;
					default:
						if (!char.IsControl(key.KeyChar))
						{
							currentInput += key.KeyChar;
							var typedFrame = RenderFrame(
								topRow, frame, currentInput, frameSize.width, frameSize.height
							);

							frameSize = (topRow, typedFrame.width, typedFrame.height);
						}

						break;
				}
			}

			var latestAdvantage = getAdvantage();
			if (latestAdvantage != lastAdvantage)
			{
				lastAdvantage = latestAdvantage;
				frame         = buildFrame(latestAdvantage);
				var updatedFrame = RenderFrame(topRow, frame, currentInput, frameSize.width, frameSize.height);
				frameSize = (topRow, updatedFrame.width, updatedFrame.height);
			}

			await Task.Delay(100);
		}
	}

	private static void PrintBoard(
		Fen               fen,
		char              playerColor,
		int               legalMoveCount,
		PositionAdvantage advantage)
	{
		foreach (string line in BuildBoardLines(fen, playerColor, legalMoveCount, advantage))
			Console.WriteLine(line);
	}

	private static void PrintGameOver(Fen fen, char playerColor)
	{
		Console.WriteLine();

		if (!string.IsNullOrWhiteSpace(fen.Checkers))
		{
			bool playerLost = fen.ActiveColor == playerColor;
			Console.WriteLine(playerLost ? "Checkmate. The engine wins." : "Checkmate. You win.");
			return;
		}

		Console.WriteLine("Stalemate.");
	}

	private static void PrintMoveHistory(UciPlayableMatchSession session)
	{
		string[] lines = session.GetMoveHistoryDisplayLines();

		foreach (string line in lines)
			Console.WriteLine(line);
	}

	private static void UndoMostRecentTurn(UciPlayableMatchSession session, PlayableMatchState state)
	{
		int undoCount = state.Fen.ActiveColor == session.PlayerColor ? 2 : 1;
		if (!session.CanUndoMoves(undoCount))
		{
			Console.WriteLine("Not enough played moves are available to undo.");
			return;
		}

		session.UndoMoves(undoCount);
		Console.WriteLine(undoCount == 1 ? "Undid the last move." : "Undid the last full turn.");
	}
}

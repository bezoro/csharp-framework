using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bezoro.Chess.UCI.Protocol.API.Common.Extensions;

/// <summary>
///     Extension methods that layer reusable move and position analysis workflows on top of <see cref="UciEngineClient" />.
/// </summary>
public static class UciEngineClientAnalysisExtensions
{
	/// <summary>
	///     Evaluates the current position and returns a player-relative advantage summary.
	/// </summary>
	/// <param name="client">Client used for the evaluation search.</param>
	/// <param name="sideToMove">Side to move for the current position: <c>w</c> or <c>b</c>.</param>
	/// <param name="playerColor">Player side: <c>w</c> or <c>b</c>.</param>
	/// <param name="moveTimeMs">Search time in milliseconds.</param>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>Normalized position advantage.</returns>
	public static async Task<PositionAdvantage> EvaluateAdvantageAsync(
		this UciEngineClient client,
		char                 sideToMove,
		char                 playerColor,
		int                  moveTimeMs = 250,
		CancellationToken    ct         = default)
	{
		if (client is null) throw new ArgumentNullException(nameof(client));
		ValidateRequest(sideToMove, playerColor, moveTimeMs, moveTimeMs, ct);

		var evaluation = await client.GoAsync(new() { MoveTimeMs = moveTimeMs }, ct).ConfigureAwait(false);
		return PositionAdvantage.FromEngineScore(
			evaluation.BestCpScore,
			evaluation.MateScore,
			sideToMove,
			playerColor
		);
	}

	/// <summary>
	///     Evaluates legal moves for the current position, using MultiPV when the engine supports it and falling back to
	///     single-move searches when necessary.
	/// </summary>
	/// <param name="client">Client used for the analysis searches.</param>
	/// <param name="sideToMove">Side to move for the current position: <c>w</c> or <c>b</c>.</param>
	/// <param name="playerColor">Player side: <c>w</c> or <c>b</c>.</param>
	/// <param name="legalMoves">Legal moves in lowercase UCI notation.</param>
	/// <param name="multiPvMoveTimeMs">Search time in milliseconds when running a MultiPV search.</param>
	/// <param name="fallbackMoveTimeMs">Per-move search time in milliseconds when falling back to single-move searches.</param>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>Move evaluations sorted by descending preference.</returns>
	public static async Task<ImmutableArray<MoveEvaluation>> AnalyzeLegalMovesAsync(
		this UciEngineClient    client,
		char                    sideToMove,
		char                    playerColor,
		IEnumerable<string>     legalMoves,
		int                     multiPvMoveTimeMs  = 3_000,
		int                     fallbackMoveTimeMs = 250,
		CancellationToken       ct                 = default)
	{
		if (client is null) throw new ArgumentNullException(nameof(client));
		if (legalMoves is null) throw new ArgumentNullException(nameof(legalMoves));
		ValidateRequest(sideToMove, playerColor, multiPvMoveTimeMs, fallbackMoveTimeMs, ct);

		ImmutableArray<string> legalMovesSnapshot = legalMoves.NormalizeUciMoves();
		if (legalMovesSnapshot.IsDefaultOrEmpty)
			return [];

		if (!TryGetMultiPvOption(client, out var multiPvOption))
		{
			return await EvaluateMovesIndividuallyAsync(
				client,
				sideToMove,
				playerColor,
				legalMovesSnapshot,
				fallbackMoveTimeMs,
				ct
			).ConfigureAwait(false);
		}

		int requestedMultiPv = legalMovesSnapshot.Length;
		if (multiPvOption.Max is int maxMultiPv)
			requestedMultiPv = Math.Min(requestedMultiPv, maxMultiPv);

		requestedMultiPv = Math.Max(1, requestedMultiPv);

		string restoreValue = string.IsNullOrWhiteSpace(multiPvOption.DefaultValue)
			? "1"
			: multiPvOption.DefaultValue;

		await client.SetOptionAsync(
			multiPvOption.Name,
			requestedMultiPv.ToString(CultureInfo.InvariantCulture),
			ct
		).ConfigureAwait(false);

		try
		{
			var result = await client.GoAsync(new() { MoveTimeMs = multiPvMoveTimeMs }, ct).ConfigureAwait(false);
			return await BuildMoveEvaluationsFromMultiPvAsync(
				client,
				result,
				sideToMove,
				playerColor,
				legalMovesSnapshot,
				fallbackMoveTimeMs,
				ct
			).ConfigureAwait(false);
		}
		finally
		{
			await client.SetOptionAsync(multiPvOption.Name, restoreValue, CancellationToken.None).ConfigureAwait(false);
		}
	}

	/// <summary>
	///     Evaluates the current position and its legal moves using a single high-quality analysis flow whenever
	///     MultiPV is available.
	/// </summary>
	public static async Task<PositionAnalysisResult> AnalyzePositionAsync(
		this UciEngineClient    client,
		char                    sideToMove,
		char                    playerColor,
		IEnumerable<string>     legalMoves,
		int                     multiPvMoveTimeMs  = 3_000,
		int                     fallbackMoveTimeMs = 250,
		CancellationToken       ct                 = default)
	{
		if (client is null) throw new ArgumentNullException(nameof(client));
		if (legalMoves is null) throw new ArgumentNullException(nameof(legalMoves));
		ValidateRequest(sideToMove, playerColor, multiPvMoveTimeMs, fallbackMoveTimeMs, ct);

		ImmutableArray<string> legalMovesSnapshot = legalMoves.NormalizeUciMoves();
		if (legalMovesSnapshot.IsDefaultOrEmpty)
			return new(PositionAdvantage.GameOver(), []);

		if (!TryGetMultiPvOption(client, out var multiPvOption))
		{
			var advantage = await client.EvaluateAdvantageAsync(
				sideToMove,
				playerColor,
				multiPvMoveTimeMs,
				ct
			).ConfigureAwait(false);

			var evaluations = await EvaluateMovesIndividuallyAsync(
				client,
				sideToMove,
				playerColor,
				legalMovesSnapshot,
				fallbackMoveTimeMs,
				ct
			).ConfigureAwait(false);

			return new(advantage, evaluations);
		}

		int requestedMultiPv = legalMovesSnapshot.Length;
		if (multiPvOption.Max is int maxMultiPv)
			requestedMultiPv = Math.Min(requestedMultiPv, maxMultiPv);

		requestedMultiPv = Math.Max(1, requestedMultiPv);

		string restoreValue = string.IsNullOrWhiteSpace(multiPvOption.DefaultValue)
			? "1"
			: multiPvOption.DefaultValue;

		await client.SetOptionAsync(
			multiPvOption.Name,
			requestedMultiPv.ToString(CultureInfo.InvariantCulture),
			ct
		).ConfigureAwait(false);

		try
		{
			var result = await client.GoAsync(new() { MoveTimeMs = multiPvMoveTimeMs }, ct).ConfigureAwait(false);
			var advantage = PositionAdvantage.FromEngineScore(
				result.BestCpScore,
				result.MateScore,
				sideToMove,
				playerColor
			);

			var evaluations = await BuildMoveEvaluationsFromMultiPvAsync(
				client,
				result,
				sideToMove,
				playerColor,
				legalMovesSnapshot,
				fallbackMoveTimeMs,
				ct
			).ConfigureAwait(false);

			return new(advantage, evaluations);
		}
		finally
		{
			await client.SetOptionAsync(multiPvOption.Name, restoreValue, CancellationToken.None).ConfigureAwait(false);
		}
	}

	private static MoveEvaluation BuildMoveEvaluation(
		string        move,
		int?          rawCpScore,
		int?          rawMateScore,
		char          sideToMove,
		char          playerColor)
	{
		var moveScore = PositionScore.FromEngineScore(
			rawCpScore,
			rawMateScore,
			sideToMove,
			playerColor
		);

		return new(move, moveScore);
	}

	private static async Task<ImmutableArray<MoveEvaluation>> BuildMoveEvaluationsFromMultiPvAsync(
		UciEngineClient        client,
		SearchResult           result,
		char                   sideToMove,
		char                   playerColor,
		ImmutableArray<string> legalMoves,
		int                    fallbackMoveTimeMs,
		CancellationToken      ct)
	{
		var capturedVariations = new Dictionary<string, PrincipalVariation>(StringComparer.Ordinal);

		foreach (var variation in result.PrincipalVariations)
		{
			if (variation.Moves.IsDefaultOrEmpty)
				continue;

			string move = variation.Moves[0];
			if (!legalMoves.ContainsUciMove(move))
				continue;

			if (!capturedVariations.TryGetValue(move, out var existing) ||
				variation.Depth > existing.Depth ||
				variation.Depth == existing.Depth && variation.SelDepth >= existing.SelDepth)
			{
				capturedVariations[move] = variation;
			}
		}

		var evaluations = ImmutableArray.CreateBuilder<MoveEvaluation>(legalMoves.Length);
		foreach (string move in legalMoves)
		{
			if (capturedVariations.TryGetValue(move, out var variation))
			{
				evaluations.Add(
					BuildMoveEvaluation(
						move,
						variation.ScoreCp,
						variation.ScoreMate,
						sideToMove,
						playerColor
					)
				);

				continue;
			}

			evaluations.Add(
				await EvaluateSingleMoveAsync(
					client,
					move,
					sideToMove,
					playerColor,
					fallbackMoveTimeMs,
					ct
				).ConfigureAwait(false)
			);
		}

		var sorted = evaluations.ToImmutable();
		return [.. sorted.OrderByDescending(static evaluation => evaluation.SortValue)];
	}

	private static async Task<ImmutableArray<MoveEvaluation>> EvaluateMovesIndividuallyAsync(
		UciEngineClient        client,
		char                   sideToMove,
		char                   playerColor,
		ImmutableArray<string> legalMoves,
		int                    fallbackMoveTimeMs,
		CancellationToken      ct)
	{
		var evaluations = ImmutableArray.CreateBuilder<MoveEvaluation>(legalMoves.Length);

		foreach (string move in legalMoves)
		{
			evaluations.Add(
				await EvaluateSingleMoveAsync(
					client,
					move,
					sideToMove,
					playerColor,
					fallbackMoveTimeMs,
					ct
				).ConfigureAwait(false)
			);
		}

		var sorted = evaluations.ToImmutable();
		return [.. sorted.OrderByDescending(static evaluation => evaluation.SortValue)];
	}

	private static async Task<MoveEvaluation> EvaluateSingleMoveAsync(
		UciEngineClient   client,
		string            move,
		char              sideToMove,
		char              playerColor,
		int               fallbackMoveTimeMs,
		CancellationToken ct)
	{
		var result = await client.GoAsync(
			new()
			{
				MoveTimeMs = fallbackMoveTimeMs,
				SearchMoves = [move]
			},
			ct
		).ConfigureAwait(false);

		return BuildMoveEvaluation(
			move,
			result.BestCpScore,
			result.MateScore,
			sideToMove,
			playerColor
		);
	}

	private static void ValidateRequest(char sideToMove, char playerColor, int moveTimeMs, int fallbackMoveTimeMs,
		CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		if (sideToMove is not ('w' or 'b')) throw new ArgumentOutOfRangeException(nameof(sideToMove));
		if (playerColor is not ('w' or 'b')) throw new ArgumentOutOfRangeException(nameof(playerColor));
		if (moveTimeMs <= 0) throw new ArgumentOutOfRangeException(nameof(moveTimeMs));
		if (fallbackMoveTimeMs <= 0) throw new ArgumentOutOfRangeException(nameof(fallbackMoveTimeMs));
	}

	private static bool TryGetMultiPvOption(UciEngineClient client, out UciEngineOption option)
	{
		if (client.TryGetOption("MultiPV", out option) && option.Type == UciOptionType.Spin)
			return true;

		option = default;
		return false;
	}
}

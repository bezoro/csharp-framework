using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.Chess.UCI.Protocol.Internal;

namespace Bezoro.Chess.UCI.Protocol.API;

/// <summary>Reviews played games using N+1 position searches and locally adjudicated terminal positions.</summary>
/// <remarks>
/// The caller owns and starts the dedicated clients. Each client belongs to one analyzer and must not be used for other work during a review.
/// Reviews on this analyzer are serialized. Independent positions can use a bounded pool of dedicated clients. Engine options enable full strength, one PV, WDL, and the configured thread/hash budgets when advertised.
/// The default probability model uses engine self-play WDL, not human winning chances or proprietary CAPS2.
/// </remarks>
public sealed class UciGameReviewAnalyzer
{
	private readonly UciEngineClient[] _clients;
	private readonly GameReviewOptions _options;
	private readonly IExpectedPointsModel _model;
	private readonly IMoveReviewAnnotationPolicy? _annotationPolicy;
	private readonly SemaphoreSlim _gate = new(1, 1);

	/// <summary>Creates a reviewer using a dedicated, already started UCI client.</summary>
	/// <param name="client">Caller-owned dedicated engine client.</param>
	/// <param name="options">Finite search budget and optional player ratings.</param>
	/// <param name="expectedPointsModel">Probability model, or the engine-WDL model when omitted.</param>
	/// <param name="annotationPolicy">Optional application policy for special annotations.</param>
	public UciGameReviewAnalyzer(
		UciEngineClient client,
		GameReviewOptions options,
		IExpectedPointsModel? expectedPointsModel = null,
		IMoveReviewAnnotationPolicy? annotationPolicy = null)
		: this(new[] { client ?? throw new ArgumentNullException(nameof(client)) }, options, expectedPointsModel, annotationPolicy) { }

	/// <summary>Creates a reviewer with a caller-owned pool of distinct, already started dedicated clients.</summary>
	/// <param name="clients">Finite client pool, copied during construction. A worker exclusively uses each active client.</param>
	/// <param name="options">Finite search, concurrency, and resource budgets. Parallelism defaults to one.</param>
	/// <param name="expectedPointsModel">Probability model, or the engine-WDL model when omitted. Called sequentially after searches.</param>
	/// <param name="annotationPolicy">Optional application policy, called sequentially after searches.</param>
	public UciGameReviewAnalyzer(
		IEnumerable<UciEngineClient> clients,
		GameReviewOptions options,
		IExpectedPointsModel? expectedPointsModel = null,
		IMoveReviewAnnotationPolicy? annotationPolicy = null)
	{
		if (clients is null) throw new ArgumentNullException(nameof(clients));
		_options = options ?? throw new ArgumentNullException(nameof(options));
		var copiedClients = new List<UciEngineClient>();
		var distinctClients = new HashSet<UciEngineClient>();
		foreach (var client in clients)
		{
			if (client is null) throw new ArgumentException("The client pool must not contain null entries.", nameof(clients));
			if (!distinctClients.Add(client))
				throw new ArgumentException("Each review worker requires a distinct dedicated client.", nameof(clients));
			copiedClients.Add(client);
		}
		if (copiedClients.Count == 0) throw new ArgumentException("At least one dedicated client is required.", nameof(clients));
		_clients = copiedClients.ToArray();
		_model = expectedPointsModel ?? EngineWdlExpectedPointsModel.Instance;
		_annotationPolicy = annotationPolicy;
	}

	/// <summary>Validates the entire game, then reviews independent positions within the configured concurrency budget and returns chronological results.</summary>
	/// <param name="initialFen">Complete starting FEN. Both kings must be present.</param>
	/// <param name="playedMoves">Complete played history in UCI notation, copied before engine work begins.</param>
	/// <param name="progress">Optional serialized, monotonic completion progress with the completed position index. Use Progress&lt;T&gt; to marshal callbacks to an engine UI thread.</param>
	/// <param name="ct">Cancellation token for validation, queued reviews, and engine searches.</param>
	/// <returns>Immutable scores, expected-points losses, classifications, and explicitly defined accuracy values.</returns>
	/// <exception cref="NotSupportedException">The default model requires WDL evidence for non-mate engine scores.</exception>
	public async Task<GameReviewResult> AnalyzeAsync(
		Fen initialFen,
		IEnumerable<string> playedMoves,
		IProgress<GameReviewProgress>? progress = null,
		CancellationToken ct = default)
	{
		if (playedMoves is null)
			throw new ArgumentNullException(nameof(playedMoves));
		ct.ThrowIfCancellationRequested();
		// Copy the cheap move list before the first await so queued reviews cannot observe caller mutations.
		var copiedMoves = new List<string>();
		foreach (string move in playedMoves)
		{
			ct.ThrowIfCancellationRequested();
			if (copiedMoves.Count >= _options.MaxGameMoves)
				throw new ArgumentException("The supplied history exceeds the configured MaxGameMoves budget.", nameof(playedMoves));
			copiedMoves.Add(LocalPositionRules.NormalizeMove(move));
		}
		string[] moves = copiedMoves.ToArray();
		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			PlayablePositionValidator.Validate(initialFen);
			var positions = new Fen[moves.Length + 1];
			var results = new PlayableMatchResult[positions.Length];
			var classifications = new MoveClassification[moves.Length];
			var repetitions = new Dictionary<string, int>(StringComparer.Ordinal);
			positions[0] = initialFen;
			var legalMoves = LocalPositionRules.GetLegalMoves(initialFen);
			for (var index = 0; index < positions.Length; index++)
			{
				ct.ThrowIfCancellationRequested();
				string key = LocalPositionRules.BuildRepetitionKey(positions[index]);
				repetitions.TryGetValue(key, out int count);
				repetitions[key] = ++count;
				results[index] = GetTerminalResult(positions[index], legalMoves, count);
				if (index == moves.Length)
					break;
				if (results[index].IsTerminal)
					throw new ArgumentException("The supplied game continues after an automatic terminal result.", nameof(playedMoves));
				positions[index + 1] = LocalPositionRules.ApplyAndClassifyMove(positions[index], moves[index],
					out classifications[index], out legalMoves);
			}

			var evaluations = await EvaluatePositionsAsync(initialFen, moves, positions, results, progress, ct)
				.ConfigureAwait(false);

			ct.ThrowIfCancellationRequested();
			var reviewed = ImmutableArray.CreateBuilder<ReviewedMove>(moves.Length);
			double whiteLoss = 0, blackLoss = 0;
			int whiteCount = 0, blackCount = 0;
			for (var index = 0; index < moves.Length; index++)
			{
				ct.ThrowIfCancellationRequested();
				var before = evaluations[index];
				var after = evaluations[index + 1];
				char side = before.Position.ActiveColor;
				int? rating = side == 'w' ? _options.WhiteRating : _options.BlackRating;
				double beforePoints = GetExpectedPoints(before, side, rating);
				double afterPoints = GetExpectedPoints(after, side, rating);
				var scoreAfter = InvertScore(after.Score);
				bool preferred = string.Equals(moves[index], before.BestMove, StringComparison.Ordinal);
				long? cpLoss = before.Score.Cp is int beforeCp && scoreAfter.Cp is int afterCp
					? preferred ? 0 : Math.Max(0L, (long)beforeCp - afterCp) : null;
				// Keep decimal probability thresholds stable when subtracting binary floating-point values.
				double loss = preferred ? 0 : Math.Round(Math.Max(0, beforePoints - afterPoints), 12);
				var move = new ReviewedMove(index + 1, side, moves[index], before, after,
					before.Score, scoreAfter, cpLoss, beforePoints, afterPoints, loss,
					MoveQualityClassifier.Classify(loss), classifications[index]);
				if (_annotationPolicy is not null)
					move = move with { Annotation = _annotationPolicy.Annotate(move, rating) };
				reviewed.Add(move);
				if (side == 'w') { whiteLoss += loss; whiteCount++; }
				else { blackLoss += loss; blackCount++; }
			}

			return new(initialFen, _options, _model.Name, evaluations, reviewed.MoveToImmutable(),
				whiteCount == 0 ? null : 100 * (1 - whiteLoss / whiteCount),
				blackCount == 0 ? null : 100 * (1 - blackLoss / blackCount));
		}
		finally
		{
			_gate.Release();
		}
	}

	private async Task<ImmutableArray<GameReviewEvaluation>> EvaluatePositionsAsync(
		Fen initialFen,
		string[] moves,
		Fen[] positions,
		PlayableMatchResult[] results,
		IProgress<GameReviewProgress>? progress,
		CancellationToken ct)
	{
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var evaluations = new GameReviewEvaluation[positions.Length];
		int searchablePositions = 0;
		foreach (var result in results)
			if (!result.IsTerminal) searchablePositions++;
		int workerCount = Math.Min(Math.Min(_options.MaxParallelPositions, _clients.Length), Math.Max(1, searchablePositions));
		// Validate all active engines' advertised ranges before any engine is reconfigured.
		for (var worker = 0; worker < workerCount && searchablePositions > 0; worker++)
		{
			ValidateResourceOption(_clients[worker], "Threads", _options.ThreadsPerEngine);
			ValidateResourceOption(_clients[worker], "Hash", _options.HashMegabytesPerEngine);
		}
		var progressSync = new object();
		int nextIndex = -1, completed = 0;
		var workers = new Task[workerCount];
		for (var worker = 0; worker < workers.Length; worker++)
			workers[worker] = RunWorkerAsync(_clients[worker]);
		await Task.WhenAll(workers).ConfigureAwait(false);
		ct.ThrowIfCancellationRequested();
		return ImmutableArray.CreateRange(evaluations);

		async Task RunWorkerAsync(UciEngineClient client)
		{
			var token = cancellation.Token;
			bool configured = false;
			try
			{
				while (true)
				{
					token.ThrowIfCancellationRequested();
					int index = Interlocked.Increment(ref nextIndex);
					if (index >= positions.Length) return;
					if (results[index].IsTerminal)
					{
						var score = results[index].IsDraw ? new PositionScore(0, null) : new PositionScore(null, 0);
						evaluations[index] = new(positions[index], score, string.Empty, null, results[index]);
					}
					else
					{
						if (!configured)
						{
							await ConfigureEngineAsync(client, token).ConfigureAwait(false);
							configured = true;
						}
						evaluations[index] = await EvaluatePositionAsync(client, initialFen, moves, positions[index], index, token)
							.ConfigureAwait(false);
					}
					// A custom progress sink need not be thread-safe. Preserve monotonically increasing counts.
					lock (progressSync)
					{
						token.ThrowIfCancellationRequested();
						progress?.Report(new(++completed, positions.Length) { PositionIndex = index });
					}
				}
			}
			catch
			{
				cancellation.Cancel();
				throw;
			}
		}
	}

	private async Task<GameReviewEvaluation> EvaluatePositionAsync(
		UciEngineClient client, Fen initialFen, string[] moves, Fen position, int index, CancellationToken ct)
	{
		await client.SetPositionAsync(initialFen, new ArraySegment<string>(moves, 0, index), ct).ConfigureAwait(false);
		var search = await client.GoAsync(_options.Search, ct).ConfigureAwait(false);
		var variation = SelectExactBestMoveVariation(search);
		// Validate engine output without trusting a protocol-valid coordinate as a legal chess move.
		LocalPositionRules.ApplyMove(position, search.BestMove);
		return new(position, new PositionScore(variation.ScoreMate.HasValue ? null : variation.ScoreCp, variation.ScoreMate),
			search.BestMove.ToLowerInvariant(), variation, default);
	}

	private static PrincipalVariation SelectExactBestMoveVariation(SearchResult search)
	{
		PrincipalVariation? selected = null;
		foreach (var variation in search.PrincipalVariations)
		{
			// A finite budget can interrupt a deeper aspiration-window iteration. Its score is only
			// a bound, even when the engine retains the same best move from a completed iteration.
			if (variation.MultiPv > 1 || variation.ScoreBound != UciScoreBound.Exact ||
				!variation.ScoreCp.HasValue && !variation.ScoreMate.HasValue || variation.ScoreMate == 0 ||
				variation.Moves.IsDefaultOrEmpty ||
				!string.Equals(variation.Moves[0], search.BestMove, StringComparison.OrdinalIgnoreCase))
				continue;
			if (!selected.HasValue || variation.Depth >= selected.Value.Depth)
				selected = variation;
		}
		return selected ?? throw new InvalidOperationException(
			"Review requires a retained exact primary-variation score matching the engine's best move. Increase the search or PV-retention budget.");
	}

	private static void ValidateResourceOption(UciEngineClient client, string name, int requested)
	{
		if (client.TryGetOption(name, out var option) &&
			(option.Min is int min && requested < min || option.Max is int max && requested > max))
			throw new ArgumentException($"The requested {name} budget is outside an engine's advertised range.", nameof(requested));
	}


	private async Task ConfigureEngineAsync(UciEngineClient client, CancellationToken ct)
	{
		if (client.TryGetOption("UCI_LimitStrength", out var strength))
			await client.SetOptionAsync(strength.Name, "false", ct).ConfigureAwait(false);
		if (client.TryGetOption("Skill Level", out var skill) && skill.Max is int maxSkill)
			await client.SetOptionAsync(skill.Name, maxSkill.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
		if (client.TryGetOption("MultiPV", out var multiPv))
			await client.SetOptionAsync(multiPv.Name, "1", ct).ConfigureAwait(false);
		if (client.TryGetOption("UCI_ShowWDL", out var wdl))
			await client.SetOptionAsync(wdl.Name, "true", ct).ConfigureAwait(false);
		if (client.TryGetOption("Threads", out var threads))
			await client.SetOptionAsync(threads.Name, _options.ThreadsPerEngine.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
		if (client.TryGetOption("Hash", out var hash))
			await client.SetOptionAsync(hash.Name, _options.HashMegabytesPerEngine.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
		await client.UciNewGameAsync(ct).ConfigureAwait(false);
	}

	private double GetExpectedPoints(GameReviewEvaluation evaluation, char side, int? rating)
	{
		if (evaluation.IsTerminal)
			return evaluation.Result.IsDraw ? 0.5 : evaluation.Result.Winner == side ? 1 : 0;
		double points = _model.GetExpectedPoints(evaluation, side, rating);
		if (double.IsNaN(points) || points < 0 || points > 1)
			throw new InvalidOperationException("The expected-points model returned a value outside [0,1].");
		return points;
	}

	private static PositionScore InvertScore(PositionScore score) =>
		new(score.Cp is int cp ? checked(-cp) : null, score.Mate is int mate ? checked(-mate) : null);

	private static PlayableMatchResult GetTerminalResult(Fen fen, ImmutableArray<string> legalMoves, int repetitions)
	{
		if (legalMoves.IsEmpty)
			return LocalPositionRules.IsCurrentPlayerInCheck(fen)
				? new(PlayableMatchResultReason.Checkmate, LocalPositionRules.Opposite(fen.ActiveColor))
				: new(PlayableMatchResultReason.Stalemate, null);
		if (LocalPositionRules.HasInsufficientMaterial(fen))
			return new(PlayableMatchResultReason.InsufficientMaterial, null);
		if (fen.HalfmoveClock >= 150)
			return new(PlayableMatchResultReason.SeventyFiveMoveRule, null);
		return repetitions >= 5 ? new(PlayableMatchResultReason.FivefoldRepetition, null) : default;
	}
}

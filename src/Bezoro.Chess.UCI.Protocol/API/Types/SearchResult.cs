using System.Collections.Generic;
using System.Collections.Immutable;
using Bezoro.Chess.UCI.Protocol.Domain.EngineClient;
using Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;

namespace Bezoro.Chess.UCI.Protocol.API.Types;

/// <summary>
///     Represents the parsed result of a completed UCI search, including the final best move and any captured PVs.
/// </summary>
public readonly record struct SearchResult
{
	/// <summary>
	///     Initializes a new instance of the SearchResult struct.
	/// </summary>
	/// <param name="ReachedDepth">The depth reached by the search.</param>
	/// <param name="ReachedSelDepth">The selective depth reached by the search.</param>
	/// <param name="MultiPvValue">The number of principal variations per depth for the search.</param>
	/// <param name="TotalNodesSearched">Total nodes searched by the search.</param>
	/// <param name="TotalTbHits">Total tablebase hits during the search.</param>
	/// <param name="TotalSearchTimeMs">Total time taken for the search in milliseconds.</param>
	/// <param name="PrincipalVariations">The collection of PVs found in the search.</param>
	/// <param name="BestMove">The best move found by the search.</param>
	/// <param name="PonderMove">The ponder move found by the search.</param>
	public SearchResult(
		uint                              ReachedDepth,
		uint                              ReachedSelDepth,
		uint                              MultiPvValue,
		ulong                             TotalNodesSearched,
		ulong                             TotalTbHits,
		ulong                             TotalSearchTimeMs,
		ImmutableArray<PrincipalVariation> PrincipalVariations,
		string                            BestMove,
		string                            PonderMove
	)
	{
		this.ReachedDepth        = ReachedDepth;
		this.ReachedSelDepth     = ReachedSelDepth;
		this.MultiPvValue        = MultiPvValue;
		this.TotalNodesSearched  = TotalNodesSearched;
		this.TotalTbHits         = TotalTbHits;
		this.TotalSearchTimeMs   = TotalSearchTimeMs;
		this.PrincipalVariations = PrincipalVariations.IsDefault ? ImmutableArray<PrincipalVariation>.Empty : PrincipalVariations;
		this.BestMove            = BestMove;
		this.PonderMove          = PonderMove;
	}

	/// <summary>True if the selected principal variation reports a mate.</summary>
	public bool HasMate => MateScore.HasValue;

	/// <summary>Gets the centipawn score of the selected principal variation, or null when absent.</summary>
	public int? BestCpScore => BestPv?.ScoreCp;

	/// <summary>Gets the mate score of the selected principal variation, or null when absent.</summary>
	public int? MateScore => BestPv?.ScoreMate;

	/// <summary>
	///     Gets the deepest, most recent primary variation, or the best available MultiPV rank when the primary is absent.
	/// </summary>
	public PrincipalVariation? BestPv
	{
		get
		{
			var principalVariations = PrincipalVariations;
			if (principalVariations.IsDefaultOrEmpty) return null;
			if (TryGetPrimaryVariation(principalVariations, out var primaryVariation))
				return primaryVariation;

			PrincipalVariation? selected = null;
			foreach (var variation in principalVariations)
			{
				if (selected is { } previous &&
					(variation.MultiPv > previous.MultiPv ||
					 (variation.MultiPv == previous.MultiPv && variation.Depth < previous.Depth))) continue;
				selected = variation;
			}

			return selected;
		}
	}

	/// <summary>
	///     Gets the principal variations captured from <c>info ... pv ...</c> lines.
	/// </summary>
	public ImmutableArray<PrincipalVariation> PrincipalVariations { get; init; } =
		ImmutableArray<PrincipalVariation>.Empty;

	/// <summary>
	///     Gets the move reported by the engine in the final <c>bestmove</c> line.
	/// </summary>
	public string BestMove { get; init; } = string.Empty;

	/// <summary>
	///     Gets the optional ponder move reported alongside <see cref="BestMove" />.
	/// </summary>
	public string PonderMove { get; init; } = string.Empty;

	/// <summary>
	///     Gets the highest observed MultiPV value.
	/// </summary>
	public uint MultiPvValue { get; init; }

	/// <summary>
	///     Gets the deepest reported search depth.
	/// </summary>
	public uint ReachedDepth { get; init; }

	/// <summary>
	///     Gets the deepest reported selective depth.
	/// </summary>
	public uint ReachedSelDepth { get; init; }

	/// <summary>
	///     Gets the highest cumulative node count reported during the search.
	/// </summary>
	public ulong TotalNodesSearched { get; init; }

	/// <summary>
	///     Gets the highest cumulative elapsed time reported during the search.
	/// </summary>
	public ulong TotalSearchTimeMs { get; init; }

	/// <summary>
	///     Gets the highest cumulative tablebase hit count reported during the search.
	/// </summary>
	public ulong TotalTbHits { get; init; }

	/// <summary>
	///     Attempts to parse a completed search transcript into a <see cref="SearchResult" />.
	/// </summary>
	/// <param name="lines">Captured engine output lines for a single search.</param>
	/// <param name="result">Parsed result when the transcript contains a valid <c>bestmove</c> line.</param>
	/// <returns><see langword="true" /> when parsing succeeds; otherwise <see langword="false" />.</returns>
	public static bool TryParse(IReadOnlyCollection<string> lines, out SearchResult result)
	{
		result = default;
		if (lines is null) return false;

		// The caller already owns this finite transcript. Preserve all supplied PVs when parsing it.
		var session = new SearchSession(false, maxCapturedVariations: int.MaxValue);
		UciBestMoveMessage? bestMove = null;
		foreach (string? line in lines)
		{
			if (string.IsNullOrWhiteSpace(line)) continue;
			if (!UciProtocolParser.TryParse(line, out var message))
			{
				if (line.AsSpan().TrimStart().StartsWith("bestmove ", StringComparison.OrdinalIgnoreCase))
					return false;
				continue;
			}

			if (message.Info is { } info) session.RecordInfo(info.Payload);
			if (message.BestMove is { } move) bestMove = move;
		}

		if (bestMove is not { } resolved) return false;
		result = session.BuildResult(resolved);
		return true;
	}

	/// <summary>True if any PV contains the given move in UCI notation.</summary>
	public bool ContainsMove(string move) => GetVariationContaining(move).HasValue;

	/// <summary>
	///     Returns the first principal variation containing the supplied move, or <see langword="null" /> when absent.
	/// </summary>
	public PrincipalVariation? GetVariationContaining(string move)
	{
		if (string.IsNullOrEmpty(move) || PrincipalVariations.IsDefaultOrEmpty) return null;
		foreach (var variation in PrincipalVariations)
		{
			if (variation.Moves.IsDefaultOrEmpty) continue;
			foreach (string candidate in variation.Moves)
				if (string.Equals(candidate, move, StringComparison.OrdinalIgnoreCase)) return variation;
		}

		return null;
	}

	/// <summary>Returns the PV that starts with the given move, or null if none.</summary>
	public PrincipalVariation? GetVariationStartingWith(string move)
	{
		if (string.IsNullOrEmpty(move) || PrincipalVariations.IsDefaultOrEmpty) return null;
		foreach (var variation in PrincipalVariations)
		{
			if (!variation.Moves.IsDefaultOrEmpty && string.Equals(variation.Moves[0], move, StringComparison.OrdinalIgnoreCase))
				return variation;
		}

		return null;
	}

	/// <summary>
	///     Deconstructs the search result into its constituent parts.
	/// </summary>
	/// <param name="reachedDepth">The depth reached by the search.</param>
	/// <param name="reachedSelDepth">The selective depth reached by the search.</param>
	/// <param name="multiPvValue">The number of principal variations per depth for the search.</param>
	/// <param name="totalNodesSearched">Total nodes searched by the search.</param>
	/// <param name="totalTbHits">Total tablebase hits during the search.</param>
	/// <param name="totalSearchTimeMs">Total time taken for the search in milliseconds.</param>
	/// <param name="principalVariations">The collection of PVs found in the search.</param>
	/// <param name="bestMove">The best move found by the search.</param>
	/// <param name="ponderMove">The ponder move found by the search.</param>
	public void Deconstruct(
		out uint                              reachedDepth,
		out uint                              reachedSelDepth,
		out uint                              multiPvValue,
		out ulong                             totalNodesSearched,
		out ulong                             totalTbHits,
		out ulong                             totalSearchTimeMs,
		out ImmutableArray<PrincipalVariation> principalVariations,
		out string                            bestMove,
		out string                            ponderMove)
	{
		reachedDepth        = ReachedDepth;
		reachedSelDepth     = ReachedSelDepth;
		multiPvValue        = MultiPvValue;
		totalNodesSearched  = TotalNodesSearched;
		totalTbHits         = TotalTbHits;
		totalSearchTimeMs   = TotalSearchTimeMs;
		principalVariations = PrincipalVariations;
		bestMove            = BestMove;
		ponderMove          = PonderMove;
	}

	private static bool TryGetPrimaryVariation(
		ImmutableArray<PrincipalVariation> principalVariations,
		out PrincipalVariation            primaryVariation)
	{
		bool found = false;
		primaryVariation = default;
		foreach (var principalVariation in principalVariations)
		{
			// Engines may omit multipv for a single-PV search. Prefer the deepest,
			// most recently reported primary line rather than the first shallow iteration.
			if (principalVariation.MultiPv > 1) continue;
			if (found && principalVariation.Depth < primaryVariation.Depth) continue;
			primaryVariation = principalVariation;
			found = true;
		}

		return found;
	}
}

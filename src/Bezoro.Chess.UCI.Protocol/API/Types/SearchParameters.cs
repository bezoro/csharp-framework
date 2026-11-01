using System.Collections.Immutable;
using Bezoro.Chess.UCI.Protocol.Domain.EngineClient;

namespace Bezoro.Chess.UCI.Protocol.API.Types;

/// <summary>
///     Defines a set of parameters for controlling an engine's search with the 'go' command.
/// </summary>
public readonly record struct SearchParameters
{
	/// <summary> Search indefinitely until a 'stop' command is sent. </summary>
	public bool Infinite { get; init; }

	/// <summary> Start the search in pondering mode. </summary>
	public bool Ponder { get; init; }

	/// <summary> Restrict search to a list of moves. </summary>
	public ImmutableArray<string> SearchMoves { get; init; }

	/// <summary> Time increment for black in milliseconds. </summary>
	public int? BlackIncrementMs { get; init; }

	/// <summary> Time remaining for black in milliseconds. </summary>
	public int? BlackTimeMs { get; init; }

	/// <summary> Search for a mate in a specific number of moves. </summary>
	public int? Mate { get; init; }

	/// <summary> Number of moves remaining until the next time control. </summary>
	public int? MovesToGo { get; init; }

	/// <summary> Search for a fixed amount of time in milliseconds. </summary>
	public int? MoveTimeMs { get; init; }

	/// <summary> Time increment for white in milliseconds. </summary>
	public int? WhiteIncrementMs { get; init; }

	/// <summary> Time remaining for white in milliseconds. </summary>
	public int? WhiteTimeMs { get; init; }

	/// <summary> Search a fixed number of nodes. </summary>
	public long? Nodes { get; init; }

	/// <summary> Search only to a specific depth. </summary>
	public uint? Depth { get; init; }

	/// <summary>
	///     Returns <see langword="true" /> when at least one explicit search limit or mode was configured.
	/// </summary>
	public bool HasExplicitLimit =>
		Infinite ||
		Ponder ||
		Depth.HasValue ||
		Nodes.HasValue ||
		Mate.HasValue ||
		MoveTimeMs.HasValue ||
		WhiteTimeMs.HasValue ||
		BlackTimeMs.HasValue;

	/// <summary>
	///     Returns <see langword="true" /> when the request restricts the search to an explicit move subset.
	/// </summary>
	public bool HasSearchMoves => !SearchMoves.IsDefaultOrEmpty;

	/// <summary>
	///     Validates the configured parameters and throws when the request is internally inconsistent or too vague.
	/// </summary>
	public void Validate()
	{
		if (!HasExplicitLimit)
			throw new ArgumentException(
				"At least one explicit go limit or mode must be provided.",
				nameof(SearchParameters)
			);

		if (Depth is 0)
			throw new ArgumentOutOfRangeException(nameof(Depth), "Depth must be greater than zero.");

		if (Nodes is <= 0)
			throw new ArgumentOutOfRangeException(nameof(Nodes), "Nodes must be greater than zero.");

		if (Mate is <= 0)
			throw new ArgumentOutOfRangeException(nameof(Mate), "Mate must be greater than zero.");

		if (MoveTimeMs is <= 0)
			throw new ArgumentOutOfRangeException(nameof(MoveTimeMs), "MoveTimeMs must be greater than zero.");

		if (WhiteTimeMs is <= 0)
			throw new ArgumentOutOfRangeException(nameof(WhiteTimeMs), "WhiteTimeMs must be greater than zero.");

		if (BlackTimeMs is <= 0)
			throw new ArgumentOutOfRangeException(nameof(BlackTimeMs), "BlackTimeMs must be greater than zero.");

		if (WhiteIncrementMs is < 0)
			throw new ArgumentOutOfRangeException(nameof(WhiteIncrementMs), "WhiteIncrementMs cannot be negative.");

		if (BlackIncrementMs is < 0)
			throw new ArgumentOutOfRangeException(nameof(BlackIncrementMs), "BlackIncrementMs cannot be negative.");

		if (MovesToGo is < 0)
			throw new ArgumentOutOfRangeException(nameof(MovesToGo), "MovesToGo cannot be negative.");

		if (!SearchMoves.IsDefaultOrEmpty)
			foreach (string move in SearchMoves)
				if (!UciCommandBuilder.IsUciMoveString(move))
					throw new ArgumentException("Every search move must be valid UCI coordinate notation.", nameof(SearchMoves));
	}
}

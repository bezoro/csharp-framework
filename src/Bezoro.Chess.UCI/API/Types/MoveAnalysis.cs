using Bezoro.Core.Extensions;
using Bezoro.Chess.UCI.API.Common.Enums;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;

namespace Bezoro.Chess.UCI.API.Types;

/// <summary>
///     Represents a legal chess move and its specific characteristics,
///     such as whether it's a capture, castling, or promotion, etc...
/// </summary>
public readonly record struct MoveAnalysis
{
	/// <summary>Gets whether the move captures an opposing piece.</summary>
	public bool      IsCapture   { get; private init; }

	/// <summary>Gets whether the move castles.</summary>
	public bool      IsCastling  { get; private init; }

	/// <summary>Gets whether the move gives check.</summary>
	public bool      IsCheck     { get; private init; }

	/// <summary>Gets whether the move is an en-passant capture.</summary>
	public bool      IsEnPassant { get; private init; }

	/// <summary>Gets whether the move checkmates the opposing king.</summary>
	public bool      IsMate      { get; private init; }

	/// <summary>Gets whether the move has no special tactical classification.</summary>
	public bool      IsNormal    { get; private init; }

	/// <summary>Gets whether the move promotes a pawn.</summary>
	public bool      IsPromotion { get; private init; }

	/// <summary>Gets whether the resulting position is stalemate.</summary>
	public bool      IsStalemate { get; private init; }

	/// <summary>Gets the engine score associated with the move.</summary>
	public MoveScore Score       { get; private init; }

	/// <summary>Gets the piece moved from the source square, when resolved.</summary>
	public Piece?    MovingPiece { get; private init; }


	/// <summary>
	///     Synchronous analyzer used when an engine-derived score is available.
	///     Delegates structural and tactical move semantics to the protocol-layer FEN classifier,
	///     then overlays the supplied engine score onto the result.
	/// </summary>
	internal static MoveAnalysis Analyze(string moveNotation, BoardState boardState, MoveScore score, bool isStalemate)
	{
		moveNotation.ThrowIfNull();
		boardState.ThrowIfNull();

		var parsedMove = ParsedMove.FromNotation(moveNotation);

		if (!boardState.TryGetPieceAt(parsedMove.From, out var movingPiece) || movingPiece is null)
			throw new ArgumentException(
				$"No piece found on square '{parsedMove.From}' for move '{moveNotation}'.",
				nameof(boardState)
			);

		var classification = boardState.Fen.ClassifyMoveFully(parsedMove.Notation);
		bool isMate = classification.IsMate;
		bool isCheck = classification.IsCheck || isMate;

		return new()
		{
			IsCapture   = classification.IsCapture,
			IsCastling  = classification.IsCastling,
			IsCheck     = isCheck,
			IsEnPassant = classification.IsEnPassant,
			IsMate      = isMate,
			IsNormal    = classification.IsNormal,
			IsPromotion = classification.IsPromotion,
			IsStalemate = classification.IsStalemate,
			Score       = score,
			MovingPiece = movingPiece
		};
	}
}

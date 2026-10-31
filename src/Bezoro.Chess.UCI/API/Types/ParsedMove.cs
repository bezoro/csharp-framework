using Bezoro.Chess.UCI.API.Common.Extensions;

namespace Bezoro.Chess.UCI.API.Types;

/// <summary>
///     Represents normalized coordinate move notation and its optional piece designators.
/// </summary>
public readonly record struct ParsedMove
{
	private ParsedMove(Piece movingPiece, Piece? promotionPiece, string from, string to, string notation, string raw)
	{
		MovingPiece    = movingPiece;
		PromotionPiece = promotionPiece;
		From           = from;
		To             = to;
		Notation       = notation;
		Raw            = raw;
	}

	/// <summary>Gets whether the notation contains a promotion piece.</summary>
	public bool IsPromotion => PromotionPiece != null;

	/// <summary>Gets the explicit moving piece, or the default piece when notation omits it.</summary>
	public Piece MovingPiece { get; }

	/// <summary>Gets the promoted piece, when present.</summary>
	public Piece? PromotionPiece { get; }

	/// <summary>Gets the source square.</summary>
	public string From           { get; }

	/// <summary>Gets normalized coordinate notation without an explicit piece designator.</summary>
	public string Notation       { get; }

	/// <summary>Gets the original notation.</summary>
	public string Raw            { get; }

	/// <summary>Gets the destination square.</summary>
	public string To             { get; }

	/// <summary>
	///     Parses strict coordinate notation containing four or five characters, optionally preceded by a piece designator.
	/// </summary>
	/// <param name="moveNotation">The notation to parse.</param>
	/// <returns>The parsed move.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="moveNotation" /> is <see langword="null" />.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="moveNotation" /> has an unsupported length.</exception>
	/// <exception cref="ArgumentException">Squares or promotion travel are malformed.</exception>
	public static ParsedMove FromNotation(string moveNotation)
	{
		if (moveNotation is null) throw new ArgumentNullException(nameof(moveNotation));
		string coordinateText = moveNotation.Trim();
		if (coordinateText.Length is < 4 or > 6)
			throw new ArgumentOutOfRangeException(nameof(moveNotation), "Expected coordinate notation and an optional leading piece.");

		Piece movingPiece = default;
		if (!UciCoordinateMove.TryParse(coordinateText, out var coordinateMove))
		{
			if (coordinateText.Length < 5 || !coordinateText[0].IsValidPieceChar() ||
				!UciCoordinateMove.TryParse(coordinateText[1..], out coordinateMove))
				throw new ArgumentException("Expected a valid UCI coordinate move and optional leading piece.", nameof(moveNotation));
			movingPiece = Piece.FromChar(coordinateText[0]);
		}

		Piece? promotionPiece = coordinateMove.PromotionPiece is char suffix
			? Piece.FromChar(coordinateMove.To.RankIndex == 7 ? char.ToUpperInvariant(suffix) : suffix)
			: null;
		string notation = coordinateMove.Notation;
		return new(movingPiece, promotionPiece, notation[..2], notation.Substring(2, 2), notation, moveNotation);
	}
}

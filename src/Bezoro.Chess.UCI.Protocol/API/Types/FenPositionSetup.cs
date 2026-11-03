using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Common.Helpers;

namespace Bezoro.Chess.UCI.Protocol.API.Types;

/// <summary>Authors a FEN position, including incomplete boards that are not yet playable.</summary>
public sealed class FenPositionSetup
{
	private readonly Dictionary<ChessSquare, char> _pieces = new();

	/// <summary>Gets the occupied squares and their standard FEN piece symbols.</summary>
	public IReadOnlyDictionary<ChessSquare, char> Pieces => _pieces;
	/// <summary>Gets or sets the active side, 'w' or 'b'.</summary>
	public char ActiveColor { get; set; } = 'w';
	/// <summary>Gets or sets the FEN castling rights, such as KQkq or '-'.</summary>
	public string CastlingRights { get; set; } = "-";
	/// <summary>Gets or sets the en-passant target square or '-'.</summary>
	public string EnPassantTarget { get; set; } = "-";
	/// <summary>Gets or sets the number of halfmoves since the last pawn move or capture.</summary>
	public int HalfmoveClock { get; set; }
	/// <summary>Gets or sets the fullmove number, starting at one.</summary>
	public int FullmoveNumber { get; set; } = 1;

	/// <summary>Sets a standard FEN piece symbol, or removes a piece when <paramref name="piece" /> is '\0'.</summary>
	/// <exception cref="ArgumentException">The symbol does not represent a chess piece.</exception>
	public void SetPiece(ChessSquare square, char piece)
	{
		if (piece == '\0') _pieces.Remove(square);
		else if ("PNBRQKpnbrqk".IndexOf(piece) >= 0) _pieces[square] = piece;
		else throw new ArgumentException("Use a standard FEN piece symbol or '\\0' to remove a piece.", nameof(piece));
	}

	/// <summary>Imports a syntactically valid FEN without requiring a playable position.</summary>
	/// <exception cref="ArgumentException">The value is not an initialized, parsed FEN.</exception>
	public static FenPositionSetup FromFen(Fen fen)
	{
		var setup = new FenPositionSetup();
		if (!Fen.Validate(fen.Raw) || !FenPiecePlacementParser.TryParse(fen.PiecePlacement, setup._pieces))
			throw new ArgumentException("The position must be a parsed FEN.", nameof(fen));
		setup.ActiveColor = fen.ActiveColor;
		setup.CastlingRights = fen.CastlingRights;
		setup.EnPassantTarget = fen.EnPassantTarget;
		setup.HalfmoveClock = fen.HalfmoveClock;
		setup.FullmoveNumber = fen.FullmoveNumber;
		return setup;
	}

	/// <summary>Builds a syntactically valid FEN without changing authored metadata or requiring playability.</summary>
	/// <exception cref="ArgumentException">Authored metadata cannot be represented by a valid FEN.</exception>
	public Fen BuildFen()
	{
		var placement = new StringBuilder();
		for (int rank = ChessSquare.BoardSize - 1; rank >= 0; rank--)
		{
			var empty = 0;
			for (var file = 0; file < ChessSquare.BoardSize; file++)
			{
				if (!_pieces.TryGetValue(new(file, rank), out char piece)) { empty++; continue; }
				if (empty > 0) { placement.Append(empty); empty = 0; }
				placement.Append(piece);
			}
			if (empty > 0) placement.Append(empty);
			if (rank > 0) placement.Append('/');
		}
		string raw = string.Join(" ", placement, ActiveColor, CastlingRights, EnPassantTarget,
			HalfmoveClock.ToString(CultureInfo.InvariantCulture), FullmoveNumber.ToString(CultureInfo.InvariantCulture));
		return Fen.Parse(raw) ?? throw new ArgumentException($"Generated FEN is invalid: {raw}");
	}

	/// <summary>Checks authored fields and the framework's playable-position constraints without using an engine.</summary>
	public FenPositionValidationResult ValidateForPlay()
	{
		var fields = ImmutableArray.CreateBuilder<string>();
		if (ActiveColor is not ('w' or 'b')) fields.Add("Side to move must be White or Black.");
		if (HalfmoveClock < 0) fields.Add("Halfmove clock cannot be negative.");
		if (FullmoveNumber < 1) fields.Add("Fullmove number must be at least 1.");
		if (EnPassantTarget != "-")
		{
			if (!ChessSquare.TryParse(EnPassantTarget, out var target))
				fields.Add("En passant target must be '-' or a valid board square.");
			else
			{
				if (target.Rank is not (3 or 6)) fields.Add("En passant target must be on rank 3 or rank 6.");
				if (ActiveColor == 'w' && target.Rank != 6)
					fields.Add("When White is to move, the en passant target must be on rank 6.");
				else if (ActiveColor == 'b' && target.Rank != 3)
					fields.Add("When Black is to move, the en passant target must be on rank 3.");
			}
		}
		try
		{
			BuildFen().ValidateForPlay();
			return new(fields.ToImmutable(), string.Empty);
		}
		catch (ArgumentException exception)
		{
			AddBoardFieldErrors(fields);
			return new(fields.ToImmutable(), exception.Message);
		}
	}

	/// <summary>Creates a validated playable setup from this position, or from an explicit history base and moves.</summary>
	/// <remarks>When a history base is supplied, it is authoritative; the editable board is not a second history snapshot.</remarks>
	/// <exception cref="ArgumentException">The position, history base, or a played move is invalid.</exception>
	public PlayableMatchSetup CreateMatchSetup(string? historyBaseFen = null, IEnumerable<string>? playedMoves = null)
	{
		Fen start;
		if (historyBaseFen is null)
		{
			var validation = ValidateForPlay();
			if (!validation.IsValid)
				throw new ArgumentException(validation.FieldErrors.IsEmpty
					? validation.PlayabilityError : string.Join("\n", validation.FieldErrors));
			start = BuildFen();
		}
		else
		{
			start = Fen.Parse(historyBaseFen) ?? throw new ArgumentException($"History base FEN is invalid: {historyBaseFen}");
			start.ValidateForPlay();
		}
		var setup = new PlayableMatchSetup(start, playedMoves);
		setup.Validate();
		return setup;
	}

	private void AddBoardFieldErrors(ImmutableArray<string>.Builder fields)
	{
		var whiteKings = 0;
		var blackKings = 0;
		foreach (var pair in _pieces)
		{
			if (pair.Value == 'K') whiteKings++;
			if (pair.Value == 'k') blackKings++;
			if (pair.Key.Rank is 1 or 8 && pair.Value is 'P' or 'p')
				fields.Add($"{(pair.Value == 'P' ? "White" : "Black")} pawns cannot be placed on rank {pair.Key.Rank}.");
		}
		if (whiteKings != 1) fields.Add("Board must contain exactly one white king.");
		if (blackKings != 1) fields.Add("Board must contain exactly one black king.");
		foreach (char right in CastlingRights ?? string.Empty)
		{
			if ("KQkq".IndexOf(right) < 0) continue;
			bool white = char.IsUpper(right), kingside = char.ToLowerInvariant(right) == 'k';
			int rank = white ? 0 : 7, rookFile = kingside ? 7 : 0;
			string color = white ? "White" : "Black", side = kingside ? "kingside" : "queenside";
			if (!_pieces.TryGetValue(new(4, rank), out char king) || king != (white ? 'K' : 'k'))
				fields.Add($"{color} {side} castling requires a {color.ToLowerInvariant()} king on e{rank + 1}.");
			if (!_pieces.TryGetValue(new(rookFile, rank), out char rook) || rook != (white ? 'R' : 'r'))
				fields.Add($"{color} {side} castling requires a {color.ToLowerInvariant()} rook on {(char)('a' + rookFile)}{rank + 1}.");
		}
	}
}

/// <summary>Separates friendly authoring-field diagnostics from the underlying playable-position rejection.</summary>
public readonly record struct FenPositionValidationResult
{
	internal FenPositionValidationResult(ImmutableArray<string> fieldErrors, string playabilityError)
	{
		FieldErrors = fieldErrors;
		PlayabilityError = playabilityError;
	}
	/// <summary>Gets diagnostics for authored fields such as kings, pawn ranks, metadata, and castling prerequisites.</summary>
	public ImmutableArray<string> FieldErrors { get; }
	/// <summary>Gets the framework playability rejection, or an empty string when the position is accepted.</summary>
	public string PlayabilityError { get; }
	/// <summary>Gets whether the authored position is playable.</summary>
	public bool IsValid => !FieldErrors.IsDefault && FieldErrors.IsEmpty && string.IsNullOrEmpty(PlayabilityError);
}

using System.Collections.Generic;
using System.Collections.Immutable;
using Bezoro.Core.Extensions;
using Bezoro.Chess.UCI.API.Common.Enums;

namespace Bezoro.Chess.UCI.API.Types;

/// <summary>
///     Represents an immutable board position decoded from a FEN value.
/// </summary>
public readonly record struct BoardState()
{
	private BoardState(Fen fen, IReadOnlyCollection<Position> positions) : this()
	{
		Fen       = fen;
		Positions = positions;
	}

	/// <summary>
	///     Gets the FEN value that produced this board state.
	/// </summary>
	public Fen Fen { get; } = Fen.Empty();

	/// <summary>
	///     Gets all 64 board-square positions.
	/// </summary>
	public IReadOnlyCollection<Position> Positions { get; } = Array.Empty<Position>();

	/// <summary>
	///     Gets the side whose turn is active in <see cref="Fen" />.
	/// </summary>
	/// <exception cref="InvalidOperationException">The FEN active-color token is unsupported.</exception>
	public PieceColor ActiveColor => Fen.ActiveColor switch
	{
		'w' => PieceColor.White,
		'b' => PieceColor.Black,
		_ => throw new InvalidOperationException(
			$"Unsupported active color '{Fen.ActiveColor}' in board state."
		)
	};

	/// <summary>
	///     Creates a board state from a valid FEN value.
	/// </summary>
	/// <param name="fen">The FEN value to decode.</param>
	/// <returns>The decoded board state, or <see langword="null" /> when <paramref name="fen" /> is invalid.</returns>
	public static BoardState? FromFen(Fen fen)
	{
		if (!Fen.Validate(fen.Raw)) return null;

		return new(fen, BuildPositionsFromFen(fen));
	}

	/// <summary>
	///     Attempts to retrieve the piece occupying a board square.
	/// </summary>
	/// <param name="squareNotation">Algebraic square notation such as <c>e4</c>.</param>
	/// <param name="piece">When successful, receives the piece on the square; otherwise, <see langword="null" />.</param>
	/// <returns><see langword="true" /> when the square exists in this state; otherwise, <see langword="false" />.</returns>
	public bool TryGetPieceAt(string squareNotation, out Piece? piece)
	{
		piece = null;
		if (squareNotation.IsNullOrEmpty()) return false;

		string normalizedSquare = squareNotation.Trim().ToLowerInvariant();
		if (normalizedSquare.Length < 2) return false;
		if (!IsValidSquare(normalizedSquare)) return false;

		foreach (var pos in Positions)
		{
			if (pos.Notation != normalizedSquare) continue;

			piece = pos.Piece;
			return true;
		}

		return false;
	}

	private static bool IsValidSquare(string sq)
	{
		if (sq.Length < 2) return false;

		char file = sq[0];
		char rank = sq[1];
		return file is >= 'a' and <= 'h' && rank is >= '1' and <= '8';
	}

	private static ImmutableArray<Position> BuildPositionsFromFen(Fen fen)
	{
		var    positions = ImmutableArray.CreateBuilder<Position>(64);
		string placement = fen.PiecePlacement;

		static string BuildSquare(int fileIndex, int rankIndex) => $"{(char)('a' + fileIndex)}{rankIndex}";

		var rank = 8;
		var file = 0;

		foreach (char token in placement)
		{
			switch (token)
			{
				case '/':
				{
					// Fill remaining squares in the rank if needed
					while (file < 8)
					{
						positions.Add(Position.Create(BuildSquare(file, rank), null));
						file++;
					}

					rank--;
					file = 0;
					continue;
				}
				case >= '1' and <= '8':
				{
					int empties = token - '0';
					for (var i = 0; i < empties && file < 8; i++)
					{
						positions.Add(Position.Create(BuildSquare(file, rank), null));
						file++;
					}

					continue;
				}
			}

			if (!char.IsLetter(token)) continue;

			var piece = Piece.FromChar(token);

			if (file >= 8) continue;

			positions.Add(Position.Create(BuildSquare(file, rank), piece));
			file++;
		}

		// Fill any remaining squares if placement didn't cover all 64
		while (rank >= 1)
		{
			while (file < 8)
			{
				positions.Add(Position.Create(BuildSquare(file, rank), null));
				file++;
			}

			rank--;
			file = 0;
		}

		return positions.MoveToImmutable();
	}
}

using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.Presentation.Internal;

// Seven bitboards keep the immutable snapshot self-contained, without a 64-element allocation.
internal readonly struct BoardPieces
{
    private readonly ulong _white, _pawns, _knights, _bishops, _rooks, _queens, _kings;

    private BoardPieces(ulong white, ulong pawns, ulong knights, ulong bishops, ulong rooks, ulong queens, ulong kings)
    {
        _white = white;
        _pawns = pawns;
        _knights = knights;
        _bishops = bishops;
        _rooks = rooks;
        _queens = queens;
        _kings = kings;
    }

    internal char GetSymbol(ChessSquare square)
    {
        ulong bit = Bit(square);
        char piece = (_pawns & bit) != 0 ? 'p' : (_knights & bit) != 0 ? 'n' : (_bishops & bit) != 0 ? 'b'
            : (_rooks & bit) != 0 ? 'r' : (_queens & bit) != 0 ? 'q' : (_kings & bit) != 0 ? 'k' : '\0';
        return piece != '\0' && (_white & bit) != 0 ? char.ToUpperInvariant(piece) : piece;
    }

    internal ChessSquare? GetKing(char side)
    {
        ulong king = _kings & (side == 'w' ? _white : ~_white);
        for (var i = 0; i < 64; i++)
            if ((king & (1UL << i)) != 0)
                return new ChessSquare(i & 7, i >> 3);
        return null;
    }

    internal static ulong Bit(ChessSquare square) => 1UL << (square.RankIndex * 8 + square.FileIndex);

    internal static BoardPieces Parse(ReadOnlySpan<char> placement)
    {
        ulong white = 0, pawns = 0, knights = 0, bishops = 0, rooks = 0, queens = 0, kings = 0;
        var rank = 7;
        var file = 0;
        foreach (char symbol in placement)
        {
            if (symbol == '/')
            {
                if (file != 8 || rank == 0) throw InvalidPlacement();
                rank--;
                file = 0;
                continue;
            }
            if (symbol is >= '1' and <= '8')
            {
                file += symbol - '0';
                if (file > 8) throw InvalidPlacement();
                continue;
            }
            if (file >= 8) throw InvalidPlacement();
            ulong bit = 1UL << (rank * 8 + file++);
            if (symbol is >= 'A' and <= 'Z') white |= bit;
            switch (char.ToLowerInvariant(symbol))
            {
                case 'p': pawns |= bit; break;
                case 'n': knights |= bit; break;
                case 'b': bishops |= bit; break;
                case 'r': rooks |= bit; break;
                case 'q': queens |= bit; break;
                case 'k': kings |= bit; break;
                default: throw InvalidPlacement();
            }
        }
        if (rank != 0 || file != 8) throw InvalidPlacement();
        return new BoardPieces(white, pawns, knights, bishops, rooks, queens, kings);
    }

    private static ArgumentException InvalidPlacement() => new("A complete valid FEN piece placement is required.");
}

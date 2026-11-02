using System.Collections.Immutable;
using System.Text;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.Study;

/// <summary>Converts legal standard-chess moves between UCI coordinates and English SAN, without an engine.</summary>
public static class ChessNotation
{
    /// <summary>Formats a legal move, including disambiguation, captures, promotion, check and mate.</summary>
    public static string ToSan(Fen position, string move)
    {
        if (move is null) throw new ArgumentNullException(nameof(move));
        string normalized = move.Trim().ToLowerInvariant();
        var legalMoves = position.GetLegalMoves();
        if (!legalMoves.Contains(normalized)) throw new ArgumentException("The move is not legal in this position.", nameof(move));
        Span<char> board = stackalloc char[64];
        ReadBoard(position, board);
        return AddCheckSuffix(position, normalized, FormatBody(normalized, board, legalMoves));
    }

    /// <summary>
    /// Resolves SAN to a legal UCI move. Accepts zeroes in castling and an omitted check suffix;
    /// a supplied check or mate suffix must be correct. Ambiguous, illegal and overspecified moves are rejected.
    /// </summary>
    public static string ParseSan(Fen position, string san)
    {
        if (san is null) throw new ArgumentNullException(nameof(san));
        string token = san.Trim().Replace('0', 'O');
        if (token.Length == 0 || token.Length > 12) throw new FormatException("Invalid SAN move.");
        bool hasSuffix = token[token.Length - 1] is '+' or '#';
        string body = hasSuffix ? token.Substring(0, token.Length - 1) : token;
        Span<char> board = stackalloc char[64];
        ReadBoard(position, board);
        var legalMoves = position.GetLegalMoves();
        foreach (string move in legalMoves)
        {
            if (!string.Equals(body, FormatBody(move, board, legalMoves), StringComparison.Ordinal)) continue;
            if (hasSuffix && token != AddCheckSuffix(position, move, body)) throw new FormatException("Incorrect SAN check or mate suffix.");
            return move;
        }
        throw new FormatException("SAN does not identify a legal move in this position.");
    }

    private static string AddCheckSuffix(Fen position, string move, string body)
    {
        var classification = position.ClassifyMoveFully(move);
        return classification.IsMate ? body + "#" : classification.IsCheck ? body + "+" : body;
    }

    private static string FormatBody(string move, ReadOnlySpan<char> board, ImmutableArray<string> legalMoves)
    {
        int from = Index(move[0], move[1]), to = Index(move[2], move[3]);
        char piece = char.ToUpperInvariant(board[from]);
        if (piece == 'K' && Math.Abs(from - to) == 2) return to > from ? "O-O" : "O-O-O";
        bool capture = board[to] != '\0' || piece == 'P' && move[0] != move[2];
        var text = new StringBuilder(12);
        if (piece != 'P')
        {
            text.Append(piece);
            bool ambiguous = false, sameFile = false, sameRank = false;
            foreach (string other in legalMoves)
            {
                if (other == move || other[2] != move[2] || other[3] != move[3] ||
                    board[Index(other[0], other[1])] != board[from]) continue;
                ambiguous = true;
                sameFile |= other[0] == move[0];
                sameRank |= other[1] == move[1];
            }
            if (ambiguous)
            {
                if (!sameFile) text.Append(move[0]);
                else if (!sameRank) text.Append(move[1]);
                else text.Append(move[0]).Append(move[1]);
            }
        }
        else if (capture) text.Append(move[0]);
        if (capture) text.Append('x');
        text.Append(move[2]).Append(move[3]);
        if (move.Length == 5) text.Append('=').Append(char.ToUpperInvariant(move[4]));
        return text.ToString();
    }

    private static int Index(char file, char rank) => (rank - '1') * 8 + file - 'a';

    private static void ReadBoard(Fen position, Span<char> board)
    {
        if (position.PiecePlacement is null) throw new ArgumentException("A parsed FEN is required.", nameof(position));
        board.Clear();
        int rank = 7, file = 0;
        foreach (char symbol in position.PiecePlacement)
        {
            if (symbol == '/') { rank--; file = 0; }
            else if (symbol is >= '1' and <= '8') file += symbol - '0';
            else board[rank * 8 + file++] = symbol;
        }
    }
}

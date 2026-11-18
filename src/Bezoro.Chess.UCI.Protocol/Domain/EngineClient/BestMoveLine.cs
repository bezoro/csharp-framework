using Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;

namespace Bezoro.Chess.UCI.Protocol.Domain.EngineClient;

/// <summary>Represents a parsed bestmove line emitted by a UCI engine.</summary>
internal readonly record struct BestMoveLine(string BestMove, string? PonderMove)
{
	public bool HasPonder => !string.IsNullOrEmpty(PonderMove);

	internal static bool HasKeyword(string line) => new UciTokens(line.AsSpan()).Consume("bestmove");

	public static bool TryParse(string line, out BestMoveLine result)
	{
		result = default;
		var tokens = new UciTokens(line.AsSpan());
		if (!tokens.Consume("bestmove")) return false;
		var best = tokens.Read();
		if (best.IsEmpty) return false;
		var ponder = tokens.Consume("ponder") ? tokens.Read() : default;
		result = new(best.ToString(), ponder.IsEmpty ? null : ponder.ToString());
		return true;
	}
}

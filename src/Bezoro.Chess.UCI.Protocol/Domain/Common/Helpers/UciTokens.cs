namespace Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;

/// <summary>Reads whitespace-delimited tokens without copying the engine's output line.</summary>
internal ref struct UciTokens(ReadOnlySpan<char> text)
{
	internal ReadOnlySpan<char> Remaining = text.TrimStart();

	internal ReadOnlySpan<char> Read()
	{
		var text = Remaining;
		int length = 0;
		while (length < text.Length && !char.IsWhiteSpace(text[length])) length++;
		Remaining = text[length..].TrimStart();
		return text[..length];
	}

	internal bool Consume(ReadOnlySpan<char> keyword)
	{
		var next = this;
		if (!next.Read().Equals(keyword, StringComparison.OrdinalIgnoreCase)) return false;
		this = next;
		return true;
	}
}

namespace Bezoro.TypingSystem.Types;

/// <summary>Immutable progress through one target. Position is the number of accepted characters.</summary>
public readonly struct TypingState
{
	/// <summary>Restores progress and mistakes. Both counts must be non-negative.</summary>
	public TypingState(int position, int mistakeCount = 0)
	{
		if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));
		if (mistakeCount < 0) throw new ArgumentOutOfRangeException(nameof(mistakeCount));
		Position = position;
		MistakeCount = mistakeCount;
	}

	/// <summary>Number of accepted characters, also the next target index.</summary>
	public int Position { get; }

	/// <summary>Number of accepted characters.</summary>
	public int CorrectCount => Position;

	/// <summary>Number of mistakes recorded by the caller.</summary>
	public int MistakeCount { get; }

	/// <summary>Initial progress with no accepted characters or mistakes.</summary>
	public static TypingState Initial => default;

	/// <summary>Checks completion. Empty targets do not complete a typing session.</summary>
	public bool IsComplete(int targetLength)
	{
		if (targetLength < Position) throw new ArgumentOutOfRangeException(nameof(targetLength));
		return targetLength > 0 && Position == targetLength;
	}

	/// <summary>Accepts one character. The caller must stop accepting after the target completes.</summary>
	public TypingState WithCorrect()
	{
		if (Position == int.MaxValue) throw new InvalidOperationException("Position cannot exceed Int32.MaxValue.");
		return new(Position + 1, MistakeCount);
	}

	/// <summary>Records one mistake without advancing.</summary>
	public TypingState WithMistake()
	{
		if (MistakeCount == int.MaxValue) throw new InvalidOperationException("Mistake count cannot exceed Int32.MaxValue.");
		return new(Position, MistakeCount + 1);
	}
}

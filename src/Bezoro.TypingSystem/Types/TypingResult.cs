namespace Bezoro.TypingSystem.Types;

/// <summary>
///     Represents the result of a typing validation operation.
/// </summary>
public readonly struct TypingResult
{
	private TypingResult(TypingValidationStatus status, char expected, int position, char input, int nextPosition, int targetLength)
	{
		Status = status;
		Expected = expected;
		Position = position;
		Input = input;
		NextPosition = nextPosition;
		TargetLength = targetLength;
	}

	/// <summary>
	///     Gets a value indicating whether the typing operation is complete.
	/// </summary>
	public bool IsComplete => Status == TypingValidationStatus.Completed;

	/// <summary>
	///     Gets a value indicating whether the input was correct.
	/// </summary>
	public bool IsCorrect => Status is TypingValidationStatus.Match or TypingValidationStatus.Completed;

	/// <summary>
	///     Gets a value indicating whether the validation failed due to an error (e.g., out of range).
	/// </summary>
	public bool IsFaulted => Status == TypingValidationStatus.EmptyTarget ||
							 Status == TypingValidationStatus.PositionOutOfRange;

	/// <summary>
	///     Gets the expected character at the validation position.
	/// </summary>
	public char Expected { get; }

	/// <summary>
	///     Gets the input character that was validated.
	/// </summary>
	public char Input { get; }

	/// <summary>
	///     Gets the next position to validate in the target sequence.
	/// </summary>
	public int NextPosition { get; }

	/// <summary>
	///     Gets the position in the target sequence where the validation occurred.
	/// </summary>
	public int Position { get; }

	/// <summary>
	///     Gets the total length of the target sequence.
	/// </summary>
	public int TargetLength { get; }

	/// <summary>
	///     Gets the status of the validation.
	/// </summary>
	public TypingValidationStatus Status { get; }

	/// <summary>
	///     Creates a successful result that completes the target sequence.
	/// </summary>
	/// <param name="expected">The expected character at the validation position.</param>
	/// <param name="position">The position in the target sequence where validation occurred.</param>
	/// <param name="input">The input character that was validated.</param>
	/// <param name="targetLength">The total length of the target sequence.</param>
	/// <returns>A completed typing result.</returns>
	public static TypingResult Completed(char expected, int position, char input, int targetLength)
	{
		ValidatePosition(position, targetLength);
		if (position != targetLength - 1)
			throw new ArgumentException("Completion must accept the final target character.", nameof(position));
		return new(TypingValidationStatus.Completed, expected, position, input, targetLength, targetLength);
	}

	/// <summary>Creates a fault result for an empty target.</summary>
	public static TypingResult EmptyTarget(int position, char input) =>
		new(TypingValidationStatus.EmptyTarget, default, position, input, 0, 0);

	/// <summary>Creates a match before the final target character. Use Completed for the final character.</summary>
	public static TypingResult Match(char expected, int position, char input, int targetLength)
	{
		ValidatePosition(position, targetLength);
		if (position == targetLength - 1)
			throw new ArgumentException("Use Completed for the final target character.", nameof(position));
		return new(TypingValidationStatus.Match, expected, position, input, position + 1, targetLength);
	}

	/// <summary>Creates a mismatch that leaves the position unchanged.</summary>
	public static TypingResult Mismatch(char expected, int position, char input, int targetLength)
	{
		ValidatePosition(position, targetLength);
		return new(TypingValidationStatus.Mismatch, expected, position, input, position, targetLength);
	}

	/// <summary>Creates a fault for an invalid index, with the recovery position clamped to the target.</summary>
	public static TypingResult PositionOutOfRange(int position, int targetLength, char input)
	{
		if (targetLength < 0) throw new ArgumentOutOfRangeException(nameof(targetLength));
		if (position >= 0 && position < targetLength)
			throw new ArgumentException("Position is within the target.", nameof(position));
		return new(TypingValidationStatus.PositionOutOfRange, default, position, input,
			targetLength == 0 ? 0 : Math.Clamp(position, 0, targetLength - 1), targetLength);
	}

	private static void ValidatePosition(int position, int targetLength)
	{
		if (targetLength <= 0) throw new ArgumentOutOfRangeException(nameof(targetLength));
		if (position < 0 || position >= targetLength) throw new ArgumentOutOfRangeException(nameof(position));
	}
}

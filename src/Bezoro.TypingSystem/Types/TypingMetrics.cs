using System.Diagnostics;

namespace Bezoro.TypingSystem.Types;

/// <summary>
///     Calculates and tracks typing performance metrics.
/// </summary>
public sealed class TypingMetrics
{
	private readonly Stopwatch _stopwatch;

	/// <summary>
	///     Initializes a new instance of the <see cref="TypingMetrics" /> class.
	/// </summary>
	public TypingMetrics()
	{
		_stopwatch = Stopwatch.StartNew();
	}

	/// <summary>
	///     Gets the typing accuracy as a value between 0 and 1.
	/// </summary>
	public double Accuracy => TotalEvaluated == 0 ? 0d : (double)CorrectInputs / TotalEvaluated;

	/// <summary>
	///     Gets the number of correct characters typed per minute.
	/// </summary>
	public double CharactersPerMinute
	{
		get
		{
			var minutes = Elapsed.TotalMinutes;
			return minutes <= 0d ? 0d : CorrectInputs / minutes;
		}
	}

	/// <summary>
	///     Gets the total time elapsed since the metrics tracking started.
	/// </summary>
	public TimeSpan Elapsed => _stopwatch.Elapsed;

	/// <summary>
	///     Gets the total number of non-faulted inputs evaluated.
	/// </summary>
	public uint TotalEvaluated => TotalInputs - FaultedInputs;

	/// <summary>
	///     Gets the number of correct inputs.
	/// </summary>
	public uint CorrectInputs { get; private set; }

	/// <summary>
	///     Gets the number of faulted inputs (e.g., out of range).
	/// </summary>
	public uint FaultedInputs { get; private set; }

	/// <summary>
	///     Gets the number of mistake inputs (mismatches).
	/// </summary>
	public uint MistakeInputs { get; private set; }

	/// <summary>
	///     Gets the total number of inputs recorded.
	/// </summary>
	public uint TotalInputs { get; private set; }

	/// <summary>
	///     Records a validation result and updates the metrics.
	/// </summary>
	/// <param name="result">The validation result to record.</param>
	public void Record(TypingResult result)
	{
		if (result.Status == TypingValidationStatus.Undefined)
			throw new ArgumentException("A recorded result must come from validation or a named factory.", nameof(result));
		if (TotalInputs == uint.MaxValue)
			throw new InvalidOperationException("Input count cannot exceed UInt32.MaxValue. Reset the metrics first.");
		TotalInputs++;

		if (result.IsFaulted)
		{
			FaultedInputs++;
			return;
		}

		if (result.IsCorrect)
			CorrectInputs++;
		else
			MistakeInputs++;
	}

	/// <summary>
	///     Resets all tracked metrics and restarts the timer.
	/// </summary>
	public void Reset()
	{
		TotalInputs   = 0;
		CorrectInputs = 0;
		MistakeInputs = 0;
		FaultedInputs = 0;
		_stopwatch.Restart();
	}
}

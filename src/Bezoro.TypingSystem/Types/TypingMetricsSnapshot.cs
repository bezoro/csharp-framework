namespace Bezoro.TypingSystem.Types;

/// <summary>An immutable, coherent observation of one typing metrics collector.</summary>
public readonly struct TypingMetricsSnapshot
{
	internal TypingMetricsSnapshot(uint total, uint correct, uint mistakes, uint faults, TimeSpan elapsed)
	{ TotalInputs = total; CorrectInputs = correct; MistakeInputs = mistakes; FaultedInputs = faults; Elapsed = elapsed; }
	/// <summary>Gets the total number of recorded inputs.</summary>
	public uint TotalInputs { get; }
	/// <summary>Gets the number of correct inputs.</summary>
	public uint CorrectInputs { get; }
	/// <summary>Gets the number of mismatches.</summary>
	public uint MistakeInputs { get; }
	/// <summary>Gets the number of validation faults.</summary>
	public uint FaultedInputs { get; }
	/// <summary>Gets elapsed time at capture.</summary>
	public TimeSpan Elapsed { get; }
	/// <summary>Gets the number of non-faulted inputs.</summary>
	public uint TotalEvaluated => TotalInputs - FaultedInputs;
	/// <summary>Gets correctly matched inputs as a fraction of evaluated inputs.</summary>
	public double Accuracy => TotalEvaluated == 0 ? 0d : (double)CorrectInputs / TotalEvaluated;
	/// <summary>Gets correctly typed characters per minute at capture.</summary>
	public double CharactersPerMinute => Elapsed.TotalMinutes <= 0d ? 0d : CorrectInputs / Elapsed.TotalMinutes;
}

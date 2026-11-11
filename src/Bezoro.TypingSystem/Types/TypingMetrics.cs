using System.Diagnostics;

namespace Bezoro.TypingSystem.Types;

/// <summary>Tracks typing performance with synchronized recording, reads and reset.</summary>
public sealed class TypingMetrics
{
	private readonly object _gate = new();
	private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
	private uint _correctInputs;
	private uint _faultedInputs;
	private uint _mistakeInputs;
	private uint _totalInputs;

	/// <summary>Gets accuracy from one coherent observation of the counters.</summary>
	public double Accuracy
	{
		get
		{
			lock (_gate)
			{
				uint evaluated = _totalInputs - _faultedInputs;
				return evaluated == 0 ? 0d : (double)_correctInputs / evaluated;
			}
		}
	}

	/// <summary>Gets correctly typed characters per minute.</summary>
	public double CharactersPerMinute
	{
		get
		{
			lock (_gate)
			{
				double minutes = _stopwatch.Elapsed.TotalMinutes;
				return minutes <= 0d ? 0d : _correctInputs / minutes;
			}
		}
	}

	/// <summary>Gets elapsed time since construction or the most recent reset.</summary>
	public TimeSpan Elapsed { get { lock (_gate) return _stopwatch.Elapsed; } }
	/// <summary>Gets the number of non-faulted inputs.</summary>
	public uint TotalEvaluated { get { lock (_gate) return _totalInputs - _faultedInputs; } }
	/// <summary>Gets the number of correct inputs.</summary>
	public uint CorrectInputs { get { lock (_gate) return _correctInputs; } }
	/// <summary>Gets the number of validation faults.</summary>
	public uint FaultedInputs { get { lock (_gate) return _faultedInputs; } }
	/// <summary>Gets the number of mismatches.</summary>
	public uint MistakeInputs { get { lock (_gate) return _mistakeInputs; } }
	/// <summary>Gets the total number of recorded inputs.</summary>
	public uint TotalInputs { get { lock (_gate) return _totalInputs; } }

	/// <summary>Copies all counters and elapsed time together for cross-thread presentation.</summary>
	public TypingMetricsSnapshot CreateSnapshot()
	{
		lock (_gate) return new(_totalInputs, _correctInputs, _mistakeInputs, _faultedInputs, _stopwatch.Elapsed);
	}

	/// <summary>Records a validated result atomically. No callbacks execute while updating counters.</summary>
	public void Record(TypingResult result)
	{
		if (result.Status == TypingValidationStatus.Undefined)
			throw new ArgumentException("A recorded result must come from validation or a named factory.", nameof(result));
		lock (_gate)
		{
			if (_totalInputs == uint.MaxValue)
				throw new InvalidOperationException("Input count cannot exceed UInt32.MaxValue. Reset the metrics first.");
			_totalInputs++;
			if (result.IsFaulted) _faultedInputs++;
			else if (result.IsCorrect) _correctInputs++;
			else _mistakeInputs++;
		}
	}

	/// <summary>Clears all counters and restarts elapsed time as one operation.</summary>
	public void Reset()
	{
		lock (_gate)
		{
			_totalInputs = _correctInputs = _mistakeInputs = _faultedInputs = 0;
			_stopwatch.Restart();
		}
	}
}

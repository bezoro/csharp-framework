using System.Diagnostics;
using Bezoro.Logging.Types;

namespace Bezoro.Logging.Utilities;

/// <summary>
///     Measures an operation and logs its elapsed time when disposed if the Bezoro.Logging assembly
///     is compiled with <c>DEBUG</c>. Otherwise, the timer stores no state and disposal performs no work.
/// </summary>
public readonly struct PerformanceTimer : IDisposable
{
	/// <summary>
	///     Initializes a new instance of the <see cref="PerformanceTimer" /> struct and starts timing
	///     when the Bezoro.Logging assembly is compiled with <c>DEBUG</c>.
	/// </summary>
	/// <param name="operationName">The name of the operation being timed.</param>
	/// <param name="category">Optional log category for the completion message.</param>
	/// <param name="contextObject">Optional context object to associate with the log entry.</param>
	public PerformanceTimer(string operationName, LogCategory? category, object? contextObject)
	{
#if DEBUG
		if (!Logger.IsEnabled(LogLevel.Info, category))
		{
			this = default;
			return;
		}
		_operationName  = operationName;
		_category       = category;
		_contextObject  = contextObject;
		_startTimestamp = Stopwatch.GetTimestamp();
#endif
	}

	/// <summary>
	///     Stops the timer and logs the elapsed time when the Bezoro.Logging assembly is compiled with
	///     <c>DEBUG</c>. Otherwise, performs no work.
	/// </summary>
	public void Dispose()
	{
#if DEBUG
		if (_operationName is null || !Logger.IsEnabled(LogLevel.Info, _category)) return;
		long   elapsed   = Stopwatch.GetTimestamp() - _startTimestamp;
		double elapsedMs = elapsed * 1000.0 / Stopwatch.Frequency;

		var message = $"{_operationName} completed in {elapsedMs:F2}ms";

		Logger.Log(message, LogLevel.Info, _category, _contextObject);
#endif
	}

#if DEBUG
	private readonly string?      _operationName;
	private readonly LogCategory? _category;
	private readonly object?      _contextObject;
	private readonly long         _startTimestamp;
#endif
}

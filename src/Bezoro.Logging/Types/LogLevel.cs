namespace Bezoro.Logging.Types;

/// <summary>
///     Defines the severity level for a log message.
/// </summary>
public enum LogLevel
{
	/// <summary>
	///     None
	/// </summary>
	None,

	/// <summary>
	///     Informational messages used for general output.
	/// </summary>
	Info,

	/// <summary>
	///     Successful operations. These have lower severity than warnings.
	/// </summary>
	Success,

	/// <summary>
	///     Warnings that indicate a potential issue or non-critical problem.
	/// </summary>
	Warning,

	/// <summary>
	///     Errors indicating a problem that has occurred but does not stop execution.
	/// </summary>
	Error,

	/// <summary>
	///     Exceptions representing severe errors or unexpected failures.
	/// </summary>
	Exception,

	/// <summary>
	///     Divider
	/// </summary>
	Divider
}

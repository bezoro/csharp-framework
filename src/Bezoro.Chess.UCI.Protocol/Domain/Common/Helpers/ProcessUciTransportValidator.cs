using System.IO;
using System.Runtime.CompilerServices;

namespace Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;

/// <summary>
///     Validator for ProcessUciTransport options and operations.
/// </summary>
internal static class ProcessUciTransportValidator
{
	/// <summary>
	///     Validates a command line to ensure it doesn't contain invalid characters.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static void ValidateCommandLine(string line)
	{
		if (line.AsSpan().IndexOfAny('\r', '\n') >= 0)
			throw new ArgumentException("Line must not contain CR or LF characters.", nameof(line));

		if (string.IsNullOrWhiteSpace(line))
			throw new ArgumentException("Command line must not be empty or whitespace.", nameof(line));
	}

	/// <summary>
	///     Validates the file system paths for the process.
	/// </summary>
	public static void ValidateFileSystem(string path, string workingDirectory, TransportStateManager stateManager)
	{
		if (!File.Exists(path))
		{
			stateManager.SetStatus(TransportStatus.Failed);
			throw new ArgumentException($"Engine executable not found at path: {path}", nameof(path));
		}

		if (Directory.Exists(workingDirectory)) return;

		stateManager.SetStatus(TransportStatus.Failed);
		throw new ArgumentException($"Working directory does not exist: {workingDirectory}", nameof(workingDirectory));
	}

	/// <summary>
	///     Validates ProcessUciTransportOptions.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static void ValidateOptions(ProcessUciTransportOptions options)
	{
		if (options.MaxIncomingLineLength <= 0)
			throw new ArgumentOutOfRangeException(nameof(options.MaxIncomingLineLength));

		if (options.ChannelCapacity <= 0)
			throw new ArgumentOutOfRangeException(
				nameof(options.ChannelCapacity),
				"ChannelCapacity must be greater than 0."
			);

		if (string.IsNullOrEmpty(options.NewLine))
			throw new ArgumentException("NewLine must be non-empty.", nameof(options.NewLine));

		if (options.QuitGracePeriod < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(
				nameof(options.QuitGracePeriod),
				"QuitGracePeriod cannot be negative."
			);

		if (options.QuitGracePeriod == default && options.QuitGracePeriodMs < 0)
			throw new ArgumentOutOfRangeException(
				nameof(options.QuitGracePeriodMs),
				"QuitGracePeriodMs cannot be negative."
			);
	}
}

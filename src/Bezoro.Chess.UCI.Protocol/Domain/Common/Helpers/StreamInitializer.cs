using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Bezoro.Chess.UCI.Protocol.Domain.Common.Helpers;

/// <summary>
///     Helper for initializing process streams.
/// </summary>
internal static class StreamInitializer
{
	/// <summary>
	///     Initializes streams for a process with the given encodings and options.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static ProcessStreams InitializeStreams(
		Process   process,
		Encoding? stdinEncoding,
		bool      redirectStandardError,
		string    newLine,
		bool      autoFlush)
	{
		var stdinEnc  = stdinEncoding ?? process.StandardInput.Encoding;
		var stdin = new StreamWriter(process.StandardInput.BaseStream, stdinEnc, 64 * 1024, leaveOpen: false)
		{
			NewLine   = newLine,
			AutoFlush = autoFlush
		};

		return new()
		{
			Stdin  = stdin,
			Stdout = process.StandardOutput,
			Stderr = redirectStandardError ? process.StandardError : null
		};
	}

	/// <summary>
	///     Safely disposes a stream writer.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static async Task SafeDisposeAsync(IAsyncDisposable? disposable)
	{
		if (disposable is null) return;

		try
		{
			await disposable.DisposeAsync().ConfigureAwait(false);
		}
		catch { }
	}

	/// <summary>
	///     Safely disposes a stream reader or writer.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static void SafeDispose(IDisposable? disposable)
	{
		if (disposable is null) return;

		try
		{
			disposable.Dispose();
		}
		catch { }
	}
}

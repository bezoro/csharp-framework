using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bezoro.Chess.UCI.Protocol.Internal;

namespace Bezoro.Chess.UCI.Internal;

/// <summary>Owns the three external-engine runtimes for an explicitly engine-backed session.</summary>
internal sealed class GameEngineClients : IAsyncDisposable
{
	internal GameEngineClients(UciEngineClient snapshot, UciPonderRuntime ponder, UciEngineClient classifier)
	{
		Snapshot = new(snapshot ?? throw new ArgumentNullException(nameof(snapshot)));
		Ponder = ponder ?? throw new ArgumentNullException(nameof(ponder));
		Classifier = new(classifier ?? throw new ArgumentNullException(nameof(classifier)));
	}

	internal SerializedUciEngineClientRuntime Snapshot { get; }
	internal UciPonderRuntime Ponder { get; }
	internal SerializedUciEngineClientRuntime Classifier { get; }
	internal bool IsStarted => Snapshot.IsStarted;
	internal bool IsHealthy => Snapshot.IsHealthy &&
		(!Ponder.IsStarted || Ponder.IsHealthy) && (!Classifier.IsStarted || Classifier.IsHealthy);

	internal static GameEngineClients Create(string enginePath, IEnumerable<string>? args, string? workingDirectory)
	{
		// A caller may supply a single-use sequence. All three processes receive the same arguments.
		var arguments = args?.ToArray();
		return new(
			new UciEngineClient(enginePath, arguments, workingDirectory),
			new UciPonderRuntime(new UciEngineClient(enginePath, arguments, workingDirectory)),
			new UciEngineClient(enginePath, arguments, workingDirectory));
	}

	public async ValueTask DisposeAsync()
	{
		try { await Classifier.DisposeAsync().ConfigureAwait(false); }
		finally
		{
			try { await Ponder.DisposeAsync().ConfigureAwait(false); }
			finally { await Snapshot.DisposeAsync().ConfigureAwait(false); }
		}
	}
}

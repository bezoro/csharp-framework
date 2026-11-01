using System.Collections.Generic;
using System.Threading.Tasks;

namespace Bezoro.Chess.UCI.Protocol.Domain.EngineClient;

/// <summary>
///     Holds shared data for a search session for the duration of an active "go ..." command.
///     Tracks output lines and bestmove completion.
/// </summary>
internal sealed class SearchSession(bool ponder, bool fireAndForget = false, int maxCapturedVariations = 4096)
{
	private readonly Queue<PrincipalVariation> _principalVariations = new();
	private readonly object                   _sync                = new();
	private          UciBestMoveMessage?      _bestMove;
	private          uint                     _multiPvValue;
	private          uint                     _reachedDepth;
	private          uint                     _reachedSelDepth;
	private          ulong                    _totalNodesSearched;
	private          ulong                    _totalSearchTimeMs;
	private          ulong                    _totalTbHits;

	/// <summary>True if this session was started in pondering mode.</summary>
	public bool Ponder { get; } = ponder;

	public bool IsFireAndForget { get; } = fireAndForget;

	/// <summary>Completes when the engine emits "bestmove".</summary>
	public TaskCompletionSource<UciBestMoveMessage> BestMoveCompletion { get; } =
		new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Completes after this session can no longer write commands or change engine activity.</summary>
	public TaskCompletionSource<object?> CleanupCompletion { get; } =
		new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>
	///     Returns the latest captured bestmove message, if any.
	/// </summary>
	public UciBestMoveMessage? BestMove
	{
		get
		{
			lock (_sync)
			{
				return _bestMove;
			}
		}
	}

	/// <summary>
	///     Builds the final immutable search result from the accumulated state.
	/// </summary>
	public SearchResult BuildResult(UciBestMoveMessage bestMove)
	{
		lock (_sync)
		{
			return new(
				_reachedDepth,
				_reachedSelDepth,
				_multiPvValue,
				_totalNodesSearched,
				_totalTbHits,
				_totalSearchTimeMs,
				[.. _principalVariations],
				bestMove.BestMove,
				bestMove.PonderMove
			);
		}
	}

	/// <summary>
	///     Completes the best move task with the supplied parsed message.
	/// </summary>
	public void CompleteBestMove(UciBestMoveMessage message)
	{
		lock (_sync)
		{
			_bestMove = message;
		}

		BestMoveCompletion.TrySetResult(message);
	}

	/// <summary>
	///     Records a principal variation emitted during the active search.
	/// </summary>
	public void RecordInfo(UciInfoPayload info)
	{
		if (IsFireAndForget) return;
		lock (_sync)
		{
			if (info.PrincipalVariation is { } principalVariation)
			{
				if (_principalVariations.Count >= maxCapturedVariations) _principalVariations.Dequeue();
				_principalVariations.Enqueue(principalVariation);
			}

			// UCI reports search-wide cumulative counters, including on lines without a PV.
			_reachedDepth = Math.Max(_reachedDepth, info.Depth ?? 0);
			_reachedSelDepth = Math.Max(_reachedSelDepth, info.SelDepth ?? 0);
			_multiPvValue = Math.Max(_multiPvValue, info.MultiPv ?? 0);
			_totalNodesSearched = Math.Max(_totalNodesSearched, info.Nodes ?? 0);
			_totalTbHits = Math.Max(_totalTbHits, info.TbHits ?? 0);
			_totalSearchTimeMs = Math.Max(_totalSearchTimeMs, info.Time ?? 0);
		}
	}
}

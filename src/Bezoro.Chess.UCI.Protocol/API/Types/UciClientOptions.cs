namespace Bezoro.Chess.UCI.Protocol.API.Types;

/// <summary>
///     Configures client-level protocol timeouts and parsing behavior.
/// </summary>
public readonly struct UciClientOptions()
{
	/// <summary>Maximum number of recent PV snapshots retained in a completed search result. Default is 4096.</summary>
	/// <remarks>Live protocol events still receive every line. Fire-and-forget searches retain no PV history.</remarks>
	public int MaxCapturedPrincipalVariations { get; init; } = 4096;

	/// <summary>
	///     Timeout used while waiting for <c>readyok</c>.
	/// </summary>
	public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(10);

	/// <summary>
	///     Timeout used while waiting for <c>uciok</c> during initialization.
	/// </summary>
	public TimeSpan UciHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

	/// <summary>
	///     Timeout used while waiting for a <c>fen ...</c> response from the non-standard <c>d</c> command.
	/// </summary>
	public TimeSpan DisplayBoardFenTimeout { get; init; } = TimeSpan.FromSeconds(2);

	/// <summary>
	///     Extra time allowed for a follow-up <c>checkers ...</c> line after the FEN is received.
	/// </summary>
	public TimeSpan DisplayBoardCheckersGracePeriod { get; init; } = TimeSpan.FromMilliseconds(750);

	/// <summary>
	///     Grace period used after sending <c>stop</c> before a search is considered timed out.
	/// </summary>
	public TimeSpan SearchStopGracePeriod { get; init; } = TimeSpan.FromSeconds(2);

	/// <summary>
	///     Timeout used for infinite searches before issuing a best-effort stop.
	/// </summary>
	public TimeSpan InfiniteSearchTimeout { get; init; } = TimeSpan.FromSeconds(120);

	/// <summary>
	///     Timeout used for node-limited searches that do not specify a depth or move time.
	/// </summary>
	public TimeSpan NodeSearchTimeout { get; init; } = TimeSpan.FromSeconds(60);

	/// <summary>
	///     Default timeout used for bounded searches that do not specify a depth, nodes, or move time.
	/// </summary>
	public TimeSpan DefaultSearchTimeout { get; init; } = TimeSpan.FromSeconds(30);

	/// <summary>
	///     Extra time added on top of <see cref="SearchParameters.MoveTimeMs" /> before a search is considered late.
	/// </summary>
	public TimeSpan MoveTimeBuffer { get; init; } = TimeSpan.FromMilliseconds(750);

	/// <summary>
	///     Minimum timeout used for move-time searches even when the requested move time is very small.
	/// </summary>
	public TimeSpan MinimumMoveTimeTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

	/// <summary>
	///     Per-ply timeout factor used for depth-limited searches.
	/// </summary>
	public int DepthTimeoutSecondsPerPly { get; init; } = 2;

	/// <summary>
	///     Minimum timeout used for depth-limited searches.
	/// </summary>
	public TimeSpan MinimumDepthSearchTimeout { get; init; } = TimeSpan.FromSeconds(10);

	/// <summary>
	///     Maximum timeout used for depth-limited searches.
	/// </summary>
	public TimeSpan MaximumDepthSearchTimeout { get; init; } = TimeSpan.FromSeconds(90);
}

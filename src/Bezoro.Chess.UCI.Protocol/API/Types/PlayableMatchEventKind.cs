namespace Bezoro.Chess.UCI.Protocol.API.Types;

/// <summary>
///     Canonical protocol-side match events emitted by <see cref="UciPlayableMatchSession" />.
/// </summary>
public enum PlayableMatchEventKind
{
	/// <summary>A new game was started from the default initial position.</summary>
	GameStarted = 0,

	/// <summary>An arbitrary position was loaded.</summary>
	PositionLoaded,

	/// <summary>The current position snapshot was refreshed.</summary>
	PositionRefreshed,

	/// <summary>A move was applied to the match state.</summary>
	MoveApplied,

	/// <summary>A promotion choice is required before the move can be applied.</summary>
	PromotionRequired,

	/// <summary>A promotion piece was chosen and the move was completed.</summary>
	PromotionChosen,

	/// <summary>One or more moves were undone.</summary>
	MovesUndone,

	/// <summary>A draw was offered by the current side.</summary>
	DrawOffered,

	/// <summary>A pending draw offer was declined.</summary>
	DrawDeclined,

	/// <summary>The match clock was paused.</summary>
	ClockPaused,

	/// <summary>The match clock was resumed.</summary>
	ClockResumed,

	/// <summary>The adjudicated result changed.</summary>
	ResultChanged,

	/// <summary>An attempted move or request was rejected as illegal.</summary>
	IllegalMoveRejected,

	/// <summary>An uncompleted promotion choice was canceled; the board and turn are unchanged.</summary>
	PromotionCanceled
}

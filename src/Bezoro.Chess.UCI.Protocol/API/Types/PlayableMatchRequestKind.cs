namespace Bezoro.Chess.UCI.Protocol.API.Types;

/// <summary>
///     Serializable request kinds supported by <see cref="UciPlayableMatchSession.ProcessAsync" />.
/// </summary>
public enum PlayableMatchRequestKind
{
	/// <summary>Starts a new standard game.</summary>
	StartNewGame = 0,

	/// <summary>Loads an explicit base position and optional move sequence.</summary>
	LoadPosition,

	/// <summary>Refreshes the current match state.</summary>
	Refresh,

	/// <summary>Applies a move for the current externally controlled side.</summary>
	ApplyMove,

	/// <summary>Completes a pending promotion.</summary>
	ChoosePromotion,

	/// <summary>Asks the current engine-controlled side to move.</summary>
	PlayControlledMove,

	/// <summary>Undoes one or more moves.</summary>
	UndoMoves,

	/// <summary>Offers a draw from the current side.</summary>
	OfferDraw,

	/// <summary>Accepts the current pending draw offer.</summary>
	AcceptDraw,

	/// <summary>Declines the current pending draw offer.</summary>
	DeclineDraw,

	/// <summary>Claims a currently claimable draw.</summary>
	ClaimDraw,

	/// <summary>Resigns on behalf of the current side.</summary>
	Resign,

	/// <summary>Pauses the active clock.</summary>
	PauseClock,

	/// <summary>Resumes the active clock.</summary>
	ResumeClock,

	/// <summary>Cancels any in-flight background analysis.</summary>
	CancelAnalysis,

	/// <summary>Dismisses a pending promotion without undoing a completed move.</summary>
	CancelPendingPromotion
}

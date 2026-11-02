namespace Bezoro.Chess.UCI.Protocol.API.Types;

/// <summary>
///     Controls how pending draw offers behave after subsequent moves.
/// </summary>
public enum PlayableMatchDrawOfferPolicy
{
	/// <summary>
	///     Clears a pending offer when its recipient completes a move. The offer survives the offering side's own move.
	/// </summary>
	ExpireOnMove = 0,

	/// <summary>
	///     Keeps the pending offer available until it is explicitly accepted or declined.
	/// </summary>
	PersistUntilResponse
}

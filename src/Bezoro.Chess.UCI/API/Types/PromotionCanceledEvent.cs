using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.UCI.API.Types;

/// <summary>Identifies a canceled promotion prompt. The position and running clock remain in place.</summary>
/// <param name="GameId">Identifier of the current gameplay session.</param>
/// <param name="PendingPromotionId">Canceled request identifier.</param>
/// <param name="Fen">Unchanged position before the move.</param>
/// <param name="TimestampUtc">Time when cancellation was accepted.</param>
public readonly record struct PromotionCanceledEvent(Guid GameId, long PendingPromotionId, Fen Fen, DateTimeOffset TimestampUtc);

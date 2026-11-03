namespace Bezoro.Chess.UCI.API.Types;

/// <summary>Identifies the match and immutable terminal state captured when the result was published.</summary>
/// <param name="GameId">Authoritative match identity, preserved even when delivery is queued after a new match starts.</param>
/// <param name="State">Terminal state of that match.</param>
public readonly record struct GameFinishedEvent(Guid GameId, UciState State);

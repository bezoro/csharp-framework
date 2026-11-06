using System.Collections.Immutable;

namespace Bezoro.Chess.UCI.API.Types;

/// <summary>
///     The completed undo's immutable state and authoritative removed move payloads.
///     Does not depend on delivery of the session's synchronization-context events.
/// </summary>
/// <param name="State">State produced by this undo command, before a later command can change the session.</param>
/// <param name="UndoneMoves">
///     Removed moves in chronological order, sharing the immutable payload array published by
///     <see cref="MoveUndoneEvent" />. Empty when no history was removed. Iterate in reverse order to animate a rewind.
/// </param>
public readonly record struct UciUndoResult(UciState State, ImmutableArray<GameMoveEvent> UndoneMoves);

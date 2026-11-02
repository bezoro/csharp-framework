using System.Collections.Immutable;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.Networking;

/// <summary>A versioned immutable position sent to an optional local or remote engine service.</summary>
/// <param name="MatchId">The host's match identity.</param>
/// <param name="Revision">Revision captured before asynchronous engine work.</param>
/// <param name="BaseFen">Position before <paramref name="Moves"/>; preserves repetition context.</param>
/// <param name="Moves">The complete chronological UCI history from the base position.</param>
public readonly record struct ChessEnginePosition(
    Guid MatchId,
    long Revision,
    Fen BaseFen,
    ImmutableArray<string> Moves);

/// <summary>A game-level engine boundary suitable for a Unity RPC or application service.</summary>
/// <remarks>The host selects and enforces search budgets. Implementations honor cancellation when their transport supports it. No process or networking stack is required by this interface.</remarks>
public interface IChessMoveService
{
    /// <summary>Finds a complete lowercase UCI move within the supplied finite search budget.</summary>
    Task<string> FindMoveAsync(ChessEnginePosition position, SearchParameters search, CancellationToken ct = default);
}

/// <summary>An independent, explicitly requested study service; playing a move never invokes a review.</summary>
/// <remarks>The service owner configures review budgets and rating models. Remote hosts must authenticate and bound requests before forwarding them.</remarks>
public interface IChessReviewService
{
    /// <summary>Reviews the complete supplied history with optional progress notifications.</summary>
    Task<GameReviewResult> ReviewAsync(
        ChessEnginePosition position,
        IProgress<GameReviewProgress>? progress = null,
        CancellationToken ct = default);
}

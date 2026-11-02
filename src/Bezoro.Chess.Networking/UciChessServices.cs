using System.Collections.Immutable;
using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.Networking;

/// <summary>Adapts a dedicated, caller-owned and already started UCI client to game-level engine requests.</summary>
/// <remarks>Requests are serialized. Do not use this client for other work. Disposal and engine process lifetime remain with the caller.</remarks>
public sealed class UciChessMoveService : IChessMoveService
{
    private readonly UciEngineClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _maximumHalfmoves;

    /// <summary>Creates an adapter without starting an engine or taking ownership of the client.</summary>
    public UciChessMoveService(UciEngineClient client, int maximumHalfmoves = 4096)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (maximumHalfmoves <= 0) throw new ArgumentOutOfRangeException(nameof(maximumHalfmoves));
        _maximumHalfmoves = maximumHalfmoves;
    }

    /// <inheritdoc />
    public async Task<string> FindMoveAsync(
        ChessEnginePosition position, SearchParameters search, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidateFiniteSearch(search);
        if (position.Moves.IsDefault || position.Moves.Length > _maximumHalfmoves)
            throw new ArgumentException("The initialized complete move history must fit the service's history budget.", nameof(position));
        // Validate before issuing any engine commands.
        position.BaseFen.ValidateForPlay();
        var currentFen = new PlayableMatchSetup(position.BaseFen, position.Moves).ResolveCurrentFen();
        if (search.HasSearchMoves) ValidateSearchMoves(search, currentFen.GetLegalMoves());
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _client.SetPositionAsync(position.BaseFen, position.Moves, ct).ConfigureAwait(false);
            return (await _client.GoAsync(search, ct).ConfigureAwait(false)).BestMove;
        }
        finally { _gate.Release(); }
    }

    internal static void ValidateFiniteSearch(SearchParameters search)
    {
        search.Validate();
        if (search.Infinite || search.Ponder ||
            !(search.Depth.HasValue || search.Nodes.HasValue || search.MoveTimeMs.HasValue ||
              (search.WhiteTimeMs.HasValue && search.BlackTimeMs.HasValue)))
            throw new ArgumentException("Playing requires a finite engine search budget.", nameof(search));
    }

    internal static void ValidateSearchMoves(SearchParameters search, ImmutableArray<string> legalMoves)
    {
        if (!search.HasSearchMoves) return;
        foreach (string requested in search.SearchMoves)
        {
            bool legal = false;
            foreach (string candidate in legalMoves)
                if (string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase)) { legal = true; break; }
            if (!legal)
                throw new ArgumentException("Every requested search move must be legal in the captured position.", nameof(search));
        }
    }
}

/// <summary>Adapts an explicitly configured game reviewer for server-side study requests.</summary>
/// <remarks>The caller owns the analyzer's clients. Their resource budgets and probability model are fixed by the analyzer, not by an untrusted request.</remarks>
public sealed class UciChessReviewService : IChessReviewService
{
    private readonly UciGameReviewAnalyzer _analyzer;

    /// <summary>Creates a review service from a caller-configured analyzer.</summary>
    public UciChessReviewService(UciGameReviewAnalyzer analyzer) =>
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));

    /// <inheritdoc />
    public Task<GameReviewResult> ReviewAsync(
        ChessEnginePosition position, IProgress<GameReviewProgress>? progress = null, CancellationToken ct = default)
    {
        if (position.Moves.IsDefault)
            throw new ArgumentException("The complete move history must be initialized.", nameof(position));
        return _analyzer.AnalyzeAsync(position.BaseFen, position.Moves, progress, ct);
    }
}

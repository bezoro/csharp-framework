using System.Collections.Generic;
using System.Threading;
using Bezoro.Chess.UCI.Protocol.API.Common.Extensions;
using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.UCI.Protocol.Internal;

/// <summary>Validates a live game's authored history without allowing play beyond an automatic result.</summary>
internal static class PlayableMatchHistoryValidator
{
    internal static Fen ValidateAndResolve(PlayableMatchSetup setup, PlayableMatchClaimableDrawPolicy claimPolicy,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (setup.PlayedMoves.IsDefault)
            throw new ArgumentException("The setup must contain an initialized move list.", nameof(setup));
        PlayablePositionValidator.Validate(setup.BaseFen);
        var current = setup.BaseFen;
        if (setup.PlayedMoves.IsEmpty)
            return current;
        var repetitions = new Dictionary<string, int>(StringComparer.Ordinal);
        string key = LocalPositionRules.BuildRepetitionKey(current);
        repetitions.Add(key, 1);
        for (int index = 0; index < setup.PlayedMoves.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int occurrences = repetitions[key];
            if (current.HalfmoveClock >= 150 || occurrences >= 5 || LocalPositionRules.HasInsufficientMaterial(current) ||
                claimPolicy == PlayableMatchClaimableDrawPolicy.Automatic && (current.HalfmoveClock >= 100 || occurrences >= 3))
                throw new ArgumentException($"Played move at index {index} follows a position that already ended the match.", nameof(setup));
            try
            {
                current = current.ApplyMove(setup.PlayedMoves[index]);
            }
            catch (InvalidOperationException error)
            {
                throw new ArgumentException($"Played move at index {index} is illegal for its position.", nameof(setup), error);
            }
            key = LocalPositionRules.BuildRepetitionKey(current);
            repetitions.TryGetValue(key, out int previousCount);
            repetitions[key] = previousCount + 1;
        }
        return current;
    }
}

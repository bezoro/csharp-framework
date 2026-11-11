namespace Bezoro.Progression;

/// <summary>Owns one byte of best-score state per story stage.</summary>
/// <remarks>Public operations are thread-safe and serialized per player. Unlocks are evaluated on demand; no update loop or cached traversal is retained.</remarks>
public sealed class StoryProgress
{
    private readonly object _sync = new();
    private readonly byte[] _stars;

    /// <summary>Creates an unplayed story for a shared immutable catalog.</summary>
    public StoryProgress(StoryCatalog catalog)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _stars = catalog.Count == 0 ? Array.Empty<byte>() : new byte[catalog.Count];
    }

    /// <summary>Gets the immutable stage graph.</summary>
    public StoryCatalog Catalog { get; }
    /// <summary>Gets the best earned score, or zero if the stage has not been completed.</summary>
    public byte GetBestStars(ProgressionId stage) { lock (_sync) return _stars[Catalog.GetIndex(stage)]; }
    /// <summary>Gets whether all stage prerequisites are currently satisfied.</summary>
    public bool IsUnlocked(ProgressionId stage) { lock (_sync) return IsUnlocked(Catalog.GetIndex(stage), _stars); }

    /// <summary>Improves an unlocked stage's best score. Returns false for locked stages or scores no better than the saved best.</summary>
    /// <remarks>The host validates gameplay before calling. A successful first completion can produce application-owned rewards in the same transaction.</remarks>
    public bool TryComplete(ProgressionId stage, byte stars, out StoryStageCompletion completion)
    {
        lock (_sync)
        {
            int index = Catalog.GetIndex(stage);
            if (stars == 0 || stars > Catalog.Stages[index].MaximumStars) throw new ArgumentOutOfRangeException(nameof(stars));
            completion = default;
            byte previous = _stars[index];
            if (stars <= previous || !IsUnlocked(index, _stars)) return false;
            _stars[index] = stars;
            completion = new StoryStageCompletion(stage, previous, stars);
            return true;
        }
    }

    /// <summary>Copies best scores in catalog order into caller-owned storage.</summary>
    public void CopyStateTo(Span<byte> destination)
    {
        lock (_sync)
        {
            if (destination.Length < _stars.Length) throw new ArgumentException("The destination is too short.", nameof(destination));
            _stars.AsSpan().CopyTo(destination);
        }
    }

    /// <summary>Validates the complete graph state and then replaces scores. A completed stage must have its prerequisites satisfied in the saved state.</summary>
    /// <remarks>The host selects the matching catalog/schema and authorizes restoration.</remarks>
    public bool TryRestore(ReadOnlySpan<byte> stars)
    {
        lock (_sync)
        {
            if (stars.Length != _stars.Length) return false;
            for (int index = 0; index < stars.Length; index++)
            {
                if (stars[index] > Catalog.Stages[index].MaximumStars) return false;
                if (stars[index] != 0 && !IsUnlocked(index, stars)) return false;
            }
            stars.CopyTo(_stars);
            return true;
        }
    }

    private bool IsUnlocked(int stageIndex, ReadOnlySpan<byte> stars)
    {
        foreach (StoryCatalog.Requirement requirement in Catalog.GetRequirements(stageIndex))
            if (stars[requirement.StageIndex] < requirement.RequiredStars) return false;
        return true;
    }
}

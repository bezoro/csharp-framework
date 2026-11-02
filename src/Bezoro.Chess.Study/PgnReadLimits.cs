namespace Bezoro.Chess.Study;

/// <summary>Explicit budgets for importing one untrusted PGN game.</summary>
public sealed class PgnReadLimits
{
    /// <summary>Creates validated input, storage, nesting and individual token budgets.</summary>
    public PgnReadLimits(int maxInputCharacters = 4_194_304, int maxNodes = 4096,
        int maxVariationDepth = 128, int maxTokenCharacters = 16_384)
    {
        if (maxInputCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxInputCharacters));
        if (maxNodes < 1) throw new ArgumentOutOfRangeException(nameof(maxNodes));
        if (maxVariationDepth < 0) throw new ArgumentOutOfRangeException(nameof(maxVariationDepth));
        if (maxTokenCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxTokenCharacters));
        MaxInputCharacters = maxInputCharacters;
        MaxNodes = maxNodes;
        MaxVariationDepth = maxVariationDepth;
        MaxTokenCharacters = maxTokenCharacters;
    }
    /// <summary>Gets the input character budget.</summary>
    public int MaxInputCharacters { get; }
    /// <summary>Gets the node budget, including the starting position.</summary>
    public int MaxNodes { get; }
    /// <summary>Gets the maximum nested recursive annotation variations (parsed iteratively).</summary>
    public int MaxVariationDepth { get; }
    /// <summary>Gets the maximum characters in one symbol, tag string or comment.</summary>
    public int MaxTokenCharacters { get; }
}

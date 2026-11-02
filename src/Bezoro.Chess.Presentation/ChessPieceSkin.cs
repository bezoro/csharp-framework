namespace Bezoro.Chess.Presentation;

/// <summary>The currently displayed board renderer.</summary>
public enum ChessBoardVisualMode
{
    /// <summary>A flat board using sprites or another 2D representation.</summary>
    TwoDimensional,
    /// <summary>A board using 3D piece models.</summary>
    ThreeDimensional
}

/// <summary>
/// Immutable metadata for a coherent piece set in both renderers. Asset keys identify a complete sprite or model set;
/// actual assets, asynchronous loading, pooling and release remain the rendering application's responsibility.
/// This descriptor is not proof of ownership or a store entitlement.
/// </summary>
public readonly struct ChessPieceSkin
{
    /// <summary>Creates a skin with stable catalog and renderer-specific asset keys.</summary>
    public ChessPieceSkin(string id, string twoDimensionalAssetKey, string threeDimensionalAssetKey)
    {
        Id = Validate(id, nameof(id));
        TwoDimensionalAssetKey = Validate(twoDimensionalAssetKey, nameof(twoDimensionalAssetKey));
        ThreeDimensionalAssetKey = Validate(threeDimensionalAssetKey, nameof(threeDimensionalAssetKey));
    }

    /// <summary>Stable cosmetic catalog identifier.</summary>
    public string Id { get; }
    /// <summary>Asset key for a set containing all twelve 2D piece appearances.</summary>
    public string TwoDimensionalAssetKey { get; }
    /// <summary>Asset key for a set containing all twelve 3D piece appearances.</summary>
    public string ThreeDimensionalAssetKey { get; }

    /// <summary>Returns the asset-set key for the requested renderer.</summary>
    public string GetAssetKey(ChessBoardVisualMode mode)
    {
        if (Id is null)
            throw new InvalidOperationException("A default skin has no asset keys.");
        return mode switch
        {
            ChessBoardVisualMode.TwoDimensional => TwoDimensionalAssetKey,
            ChessBoardVisualMode.ThreeDimensional => ThreeDimensionalAssetKey,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private static string Validate(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A nonempty identifier is required.", name) : value;
}

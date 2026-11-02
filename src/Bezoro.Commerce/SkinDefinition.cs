namespace Bezoro.Commerce;

/// <summary>An immutable cosmetic asset set; asset identifiers are resolved by the host's rendering system.</summary>
public readonly struct SkinDefinition
{
    /// <summary>Creates a skin with at least one supported presentation. An asset can reference a complete chess piece set.</summary>
    public SkinDefinition(string skinId, string? asset2D, string? asset3D)
    {
        Identifiers.Require(skinId, nameof(skinId));
        if (asset2D is not null) Identifiers.Require(asset2D, nameof(asset2D));
        if (asset3D is not null) Identifiers.Require(asset3D, nameof(asset3D));
        if (asset2D is null && asset3D is null) throw new ArgumentException("A skin must have at least one presentation asset.");
        SkinId = skinId;
        Asset2D = asset2D;
        Asset3D = asset3D;
    }

    /// <summary>Gets the stable, case-sensitive skin identifier; null for the default value.</summary>
    public string SkinId { get; }
    /// <summary>Gets an optional identifier for the two-dimensional asset set.</summary>
    public string? Asset2D { get; }
    /// <summary>Gets an optional identifier for the three-dimensional asset set.</summary>
    public string? Asset3D { get; }
    /// <summary>Gets whether this value was initialized through its constructor.</summary>
    public bool IsValid => SkinId is not null;
}

internal static class Identifiers
{
    internal static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A nonempty, case-sensitive identifier is required.", name);
    }
}

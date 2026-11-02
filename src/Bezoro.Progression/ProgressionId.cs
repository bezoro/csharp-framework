namespace Bezoro.Progression;

/// <summary>Identifies application-owned progression content. Zero is reserved for an invalid identifier.</summary>
/// <remarks>Identifiers carry no catalog or player identity; the application owns their meaning and persistence.</remarks>
public readonly record struct ProgressionId
{
    /// <summary>Creates a nonzero content identifier.</summary>
    public ProgressionId(uint value)
    {
        if (value == 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }

    /// <summary>Gets the application-defined numeric identifier.</summary>
    public uint Value { get; }
    /// <summary>Gets whether this value is a nondefault identifier.</summary>
    public bool IsValid => Value != 0;
}

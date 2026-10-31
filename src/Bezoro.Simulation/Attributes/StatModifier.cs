namespace Bezoro.Simulation.Attributes;

/// <summary>Specifies a modifier's contribution to the stat formula.</summary>
public enum StatModifierOperation
{
    /// <summary>Adds a flat amount before percentage and multiplicative contributions.</summary>
    Add,
    /// <summary>Adds a fractional percentage. For example, 0.25 represents an increase of 25 percent.</summary>
    AddPercent,
    /// <summary>Multiplies by a nonnegative factor. For example, 1.25 represents an increase of 25 percent.</summary>
    Multiply,
    /// <summary>Replaces the formula result before bounds are applied.</summary>
    Override
}

/// <summary>A finite immutable contribution to one attribute's evaluation.</summary>
public readonly struct StatModifier
{
    /// <summary>Creates a modifier. Priority is used only to choose between overrides.</summary>
    public StatModifier(StatModifierOperation operation, double value, int priority = 0)
    {
        if (unchecked((uint)operation) > (uint)StatModifierOperation.Override)
            throw new ArgumentOutOfRangeException(nameof(operation));
        StatDefinition.RequireFinite(value, nameof(value));
        if (operation == StatModifierOperation.Multiply && value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "A multiplication factor must be nonnegative.");
        Operation = operation;
        Value = value;
        Priority = priority;
    }

    // Keep the 64-bit value before the two 32-bit fields to avoid alignment padding in modifier arrays.
    /// <summary>Gets the amount, fractional percentage, factor, or override value.</summary>
    public double Value { get; }

    /// <summary>Gets the operation. The default modifier is an additive zero.</summary>
    public StatModifierOperation Operation { get; }

    /// <summary>Gets the override priority. Higher priorities win; equal priorities use the last modifier.</summary>
    public int Priority { get; }
}

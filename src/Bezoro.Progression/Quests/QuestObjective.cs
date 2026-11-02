namespace Bezoro.Progression;

/// <summary>Requires a positive amount of one application-defined event counter.</summary>
/// <remarks>Use different counter identifiers for different filters, such as wins, ranked wins, or completed lessons.</remarks>
public readonly record struct QuestObjective
{
    /// <summary>Creates an objective for a valid counter and a positive target.</summary>
    public QuestObjective(ProgressionId counter, long target)
    {
        if (!counter.IsValid) throw new ArgumentException("A valid counter identifier is required.", nameof(counter));
        if (target <= 0) throw new ArgumentOutOfRangeException(nameof(target));
        Counter = counter;
        Target = target;
    }

    /// <summary>Gets the event counter to observe.</summary>
    public ProgressionId Counter { get; }
    /// <summary>Gets the positive amount needed for completion.</summary>
    public long Target { get; }
}

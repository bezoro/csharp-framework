using Bezoro.StateMachines;

namespace Bezoro.Statecharts;

/// <summary>Declares states and an initial state within one construction-time region.</summary>
/// <remarks>Regions obtained from the same parent state run in parallel. The default region is invalid.</remarks>
public readonly struct StatechartRegion
{
    private readonly StatechartBuilder? _owner;
    internal StatechartRegion(StatechartBuilder owner, int index) { _owner = owner; Index = index; }
    internal int Index { get; }

    /// <summary>Registers a state belonging to this region.</summary>
    /// <exception cref="InvalidOperationException">This is the default region.</exception>
    public StateHandle State() => Owner.AddState(Index);

    /// <summary>Selects the initial state for this region. Later calls replace the selection for future builds.</summary>
    /// <exception cref="InvalidOperationException">This is the default region.</exception>
    /// <exception cref="ArgumentException">The state is default, foreign, or belongs to another region.</exception>
    public void StartIn(StateHandle state) => Owner.StartIn(Index, state);

    private StatechartBuilder Owner => _owner ?? throw new InvalidOperationException("Obtain a region from StatechartBuilder.Region before using it.");
}

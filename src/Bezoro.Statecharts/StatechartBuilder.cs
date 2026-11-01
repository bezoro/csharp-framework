using Bezoro.Conditions;
using Bezoro.StateMachines;

namespace Bezoro.Statecharts;

/// <summary>Declares hierarchy and parallel regions through ownership relations, without coordinating table indices.</summary>
/// <remarks>
/// Each region requires an explicit initial state. Multiple root regions, or regions of the same state, run in parallel.
/// Build returns an immutable snapshot. The mutable builder and its handles are construction-only and are not retained by definitions.
/// </remarks>
public sealed class StatechartBuilder
{
    private readonly StateMachineBuilder _identifiers = new();
    private readonly List<ChartState> _states = new();
    private readonly List<RegionDeclaration> _regions = new();
    private readonly List<ChartTransition> _transitions = new();
    private int _signalCount;

    /// <summary>Registers an always-enabled root region.</summary>
    public StatechartRegion Region() => AddRegion(null);

    /// <summary>Registers a child region enabled while the supplied parent state is active.</summary>
    /// <exception cref="ArgumentException">The parent is default or belongs to another builder.</exception>
    public StatechartRegion Region(StateHandle parent) => AddRegion(Resolve(parent));

    /// <summary>Registers an input signal accepted by the chart.</summary>
    public SignalHandle Signal()
    {
        SignalHandle signal = _identifiers.Signal();
        _signalCount++;
        return signal;
    }

    /// <summary>Declares a guarded or unconditional transition with explicit lifecycle semantics and priority.</summary>
    /// <exception cref="ArgumentException">A handle is default or belongs to another builder, or the kind is unknown.</exception>
    public void Transition(StateHandle from, SignalHandle on, StateHandle to, ConditionProgram? guard = null,
        int priority = 0, ChartTransitionKind kind = ChartTransitionKind.External)
    {
        StateId source = Resolve(from);
        SignalId signal = Resolve(on);
        StateId target = Resolve(to);
        if (kind is < ChartTransitionKind.External or > ChartTransitionKind.Internal)
            throw new ArgumentException("Unknown transition kind.", nameof(kind));
        _transitions.Add(new ChartTransition(source, signal, target, guard, priority, kind));
    }

    /// <summary>Resolves a state handle from this chart to its stable compact runtime identifier.</summary>
    /// <exception cref="ArgumentException">The handle is default or belongs to another builder.</exception>
    public StateId Resolve(StateHandle state) => _identifiers.Resolve(state);

    /// <summary>Resolves a signal handle from this chart to its stable compact runtime identifier.</summary>
    /// <exception cref="ArgumentException">The handle is default or belongs to another builder.</exception>
    public SignalId Resolve(SignalHandle signal) => _identifiers.Resolve(signal);

    /// <summary>Compiles a snapshot after every region has selected its initial state.</summary>
    /// <exception cref="InvalidOperationException">A region has no initial state, or there are no regions.</exception>
    /// <exception cref="ArgumentException">A transition has an invalid lifecycle domain.</exception>
    public StatechartDefinition Build()
    {
        if (_regions.Count == 0) throw new InvalidOperationException("Declare a root region before building.");
        var regions = new ChartRegion[_regions.Count];
        for (int index = 0; index < regions.Length; index++)
        {
            RegionDeclaration declaration = _regions[index];
            if (!declaration.Initial.HasValue)
                throw new InvalidOperationException("Select each region's initial state with StartIn before building.");
            regions[index] = new ChartRegion(declaration.Initial.Value, declaration.Parent);
        }
        return new StatechartDefinition(_states.ToArray(), regions, _transitions.ToArray(), _signalCount);
    }

    internal StateHandle AddState(int region)
    {
        StateHandle state = _identifiers.State();
        _states.Add(new ChartState(new RegionId(region)));
        return state;
    }

    internal void StartIn(int region, StateHandle state)
    {
        StateId id = Resolve(state);
        if (_states[id.Value].Region.Value != region)
            throw new ArgumentException("The initial state must belong to this region.", nameof(state));
        RegionDeclaration declaration = _regions[region];
        declaration.Initial = id;
        _regions[region] = declaration;
    }

    private StatechartRegion AddRegion(StateId? parent)
    {
        int index = _regions.Count;
        _regions.Add(new RegionDeclaration(parent));
        return new StatechartRegion(this, index);
    }

    private struct RegionDeclaration
    {
        internal RegionDeclaration(StateId? parent) { Parent = parent; Initial = null; }
        internal StateId? Parent;
        internal StateId? Initial;
    }
}

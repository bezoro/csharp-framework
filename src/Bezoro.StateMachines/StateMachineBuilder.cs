namespace Bezoro.StateMachines;

/// <summary>Declares states and signals without assigning numeric identifiers or counts by hand.</summary>
/// <remarks>
/// The builder owns construction handles. Each Build returns an independent immutable snapshot.
/// Later declarations do not change earlier definitions. Builders are not synchronized or retained by definitions.
/// </remarks>
public sealed class StateMachineBuilder
{
    private readonly List<StateTransition> _transitions = new();
    private int _stateCount;
    private int _signalCount;
    private StateId? _initialState;

    /// <summary>Registers a state. Retain the returned handle in a variable describing its meaning.</summary>
    public StateHandle State()
    {
        int index = _stateCount;
        _stateCount = checked(index + 1);
        return new StateHandle(this, index);
    }

    /// <summary>Registers an input signal.</summary>
    public SignalHandle Signal()
    {
        int index = _signalCount;
        _signalCount = checked(index + 1);
        return new SignalHandle(this, index);
    }

    /// <summary>Selects the initial state. A subsequent call replaces the selection for future builds.</summary>
    /// <exception cref="ArgumentException">The handle is default or belongs to another builder.</exception>
    public void StartIn(StateHandle state) => _initialState = Resolve(state);

    /// <summary>Declares that an input in the source state goes to the target state, including explicit self transitions.</summary>
    /// <exception cref="ArgumentException">A handle is default or belongs to another builder.</exception>
    public void Transition(StateHandle from, SignalHandle on, StateHandle to)
    {
        StateId source = Resolve(from);
        SignalId signal = Resolve(on);
        StateId target = Resolve(to);
        _transitions.Add(new StateTransition(source, signal, target));
    }

    /// <summary>Resolves one of this builder's state handles to its stable runtime identifier.</summary>
    /// <exception cref="ArgumentException">The handle is default or belongs to another builder.</exception>
    public StateId Resolve(StateHandle state)
    {
        if (!ReferenceEquals(state.Owner, this))
            throw new ArgumentException("The state handle must belong to this builder.", nameof(state));
        return new StateId(state.Index);
    }

    /// <summary>Resolves one of this builder's signal handles to its stable runtime identifier.</summary>
    /// <exception cref="ArgumentException">The handle is default or belongs to another builder.</exception>
    public SignalId Resolve(SignalHandle signal)
    {
        if (!ReferenceEquals(signal.Owner, this))
            throw new ArgumentException("The signal handle must belong to this builder.", nameof(signal));
        return new SignalId(signal.Index);
    }

    /// <summary>Compiles a snapshot, rejecting missing initial state and duplicate source/signal pairs.</summary>
    /// <exception cref="InvalidOperationException">StartIn has not selected an initial state.</exception>
    public StateMachineDefinition Build()
    {
        if (!_initialState.HasValue)
            throw new InvalidOperationException("Select the initial state with StartIn before building.");
        return new StateMachineDefinition(_stateCount, _signalCount, _transitions.ToArray(), _initialState.Value);
    }
}

using Bezoro.BehaviorTrees;
using Bezoro.StateMachines;

namespace Bezoro.Behaviors;

/// <summary>Adapts the lightweight flat transition definition to optional owned state behaviors.</summary>
/// <remarks>
/// The flat definition stays independent of composition. This adapter stores only its bindings and one active child.
/// Signals are explicit; an empty final-state mapping leaves the machine Running indefinitely.
/// </remarks>
public sealed class StateMachineBehavior<TContext> : Behavior<TContext>
{
    private readonly StateBehaviorBinding<TContext>[] _bindings;
    private readonly StateId[] _successStates;
    private readonly StateId[] _failureStates;
    private int _activeBinding = -1;
    private ulong _activationVersion;
    private SignalId? _completion;
    private bool _childFinished;
    private bool _stateActive;
    private BehaviorStatus _terminal;

    /// <summary>Creates a flat machine adapter with optional child bindings and terminal states.</summary>
    public StateMachineBehavior(StateMachineDefinition definition,
        ReadOnlySpan<StateBehaviorBinding<TContext>> bindings = default,
        ReadOnlySpan<StateId> successStates = default, ReadOnlySpan<StateId> failureStates = default,
        BehaviorCompositionLimits limits = default)
        : this(CreateSetup(definition, bindings, successStates, failureStates, limits), limits) { }

    private StateMachineBehavior(Setup setup, BehaviorCompositionLimits limits) : base(setup.Children, limits, true)
    {
        Definition = setup.Definition;
        _bindings = setup.Bindings;
        _successStates = setup.Success;
        _failureStates = setup.Failure;
        CurrentState = Definition.InitialState;
    }

    /// <summary>Gets the shared immutable flat definition.</summary>
    public StateMachineDefinition Definition { get; }
    /// <summary>Gets the current numeric state. An inactive adapter retains its last state for inspection.</summary>
    public StateId CurrentState { get; private set; }
    /// <summary>Gets this instance's monotonically increasing state-entry version, including self reentry.</summary>
    public ulong ActivationVersion => _activationVersion;
    /// <summary>Gets whether the current state entry is live. Exit invalidates it before child cancellation callbacks.</summary>
    public bool IsStateActive => _stateActive;
    /// <summary>Gets the configured child bindings ordered by state ID.</summary>
    public ReadOnlySpan<StateBehaviorBinding<TContext>> Bindings => _bindings;

    /// <summary>Delivers one explicit signal. Every matched transition, including self transitions, exits and reenters its state.</summary>
    public StateStep Dispatch(SignalId signal, ref TContext context)
    {
        BeginSignalOperation();
        try { return DispatchCore(signal, ref context); }
        finally { EndSignalOperation(); }
    }

    private StateStep DispatchCore(SignalId signal, ref TContext context)
    {
        // Argument validation and transition lookup occur before lifecycle effects.
        StateStep step = Definition.Step(CurrentState, signal);
        if (!step.HasTransition) return step;
        if (_activationVersion == ulong.MaxValue) throw new InvalidOperationException("State activation identity is exhausted.");
        try { Apply(step, ref context); }
        catch (Exception failure)
        {
            var cleanup = FaultCleanup(ref context);
            if (cleanup is not null) throw new AggregateException("Signal processing and cleanup both failed.", failure, cleanup);
            throw;
        }
        return step;
    }

    /// <summary>Delivers a signal only when its originating state-entry version is still current in this instance.</summary>
    /// <remarks>Versions are instance-local, so the caller must retain the originating adapter reference as well.</remarks>
    public bool TryDispatch(ulong activationVersion, SignalId signal, ref TContext context)
    {
        if (IsExecutingOnCurrentThread && !IsCurrentSignal(activationVersion)) return false;
        BeginSignalOperation(requireRunning: false);
        try
        {
            if (!IsCurrentSignal(activationVersion)) return false;
            DispatchCore(signal, ref context);
            return true;
        }
        finally { EndSignalOperation(); }
    }

    private bool IsCurrentSignal(ulong activationVersion) => CanAcceptSignals && _stateActive &&
        activationVersion != 0 && activationVersion == _activationVersion;

    /// <inheritdoc />
    protected override void OnStart(ref TContext context)
    {
        if (_activationVersion == ulong.MaxValue) throw new InvalidOperationException("State activation identity is exhausted.");
        CurrentState = Definition.InitialState;
        EnterState(ref context);
    }

    /// <inheritdoc />
    protected override BehaviorStatus OnTick(ref TContext context, ref BehaviorBudget budget)
    {
        if (_terminal != BehaviorStatus.Running) return _terminal;
        if (_completion.HasValue)
        {
            if (!budget.TryConsume()) { IsBudgetSuspended = true; return BehaviorStatus.Running; }
            var signal = _completion.Value;
            _completion = null;
            StateStep step = Definition.Step(CurrentState, signal);
            if (step.HasTransition)
            {
                if (_activationVersion == ulong.MaxValue) throw new InvalidOperationException("State activation identity is exhausted.");
                Apply(step, ref context);
            }
            return _terminal;
        }
        if (_activeBinding < 0 || _childFinished) return BehaviorStatus.Running;
        if (budget.Remaining == 0) { IsBudgetSuspended = true; return BehaviorStatus.Running; }
        var binding = _bindings[_activeBinding];
        BehaviorStatus child = TickChild(binding.Child, ref context, ref budget);
        IsBudgetSuspended = binding.Child.IsBudgetSuspended;
        if (child == BehaviorStatus.Running) return child;
        _childFinished = true;
        _completion = child == BehaviorStatus.Success ? binding.SuccessSignal : binding.FailureSignal;
        if (!_completion.HasValue) return BehaviorStatus.Running;
        if (!budget.TryConsume()) { IsBudgetSuspended = true; return BehaviorStatus.Running; }
        var completedSignal = _completion.Value;
        _completion = null;
        StateStep completed = Definition.Step(CurrentState, completedSignal);
        if (completed.HasTransition)
        {
            if (_activationVersion == ulong.MaxValue) throw new InvalidOperationException("State activation identity is exhausted.");
            Apply(completed, ref context);
        }
        return _terminal;
    }

    /// <inheritdoc />
    protected override void OnStop(ref TContext context)
    {
        _stateActive = false;
        _completion = null;
        _childFinished = false;
        int binding = _activeBinding;
        _activeBinding = -1;
        if (binding >= 0) StopChild(_bindings[binding].Child, ref context);
    }

    /// <inheritdoc />
    protected override bool IsChildScopeLive(int childIndex) => _stateActive && _activeBinding == childIndex;

    private void Apply(StateStep step, ref TContext context)
    {
        _stateActive = false;
        _completion = null;
        int previous = _activeBinding;
        _activeBinding = -1;
        if (previous >= 0) StopChild(_bindings[previous].Child, ref context);
        CurrentState = step.CurrentState;
        EnterState(ref context);
    }

    private void EnterState(ref TContext context)
    {
        _activationVersion++;
        _stateActive = true;
        _completion = null;
        _childFinished = false;
        _terminal = Contains(_failureStates, CurrentState) ? BehaviorStatus.Failure :
            Contains(_successStates, CurrentState) ? BehaviorStatus.Success : BehaviorStatus.Running;
        _activeBinding = FindBinding(CurrentState);
        if (_activeBinding >= 0) StartChild(_bindings[_activeBinding].Child, ref context);
    }

    private int FindBinding(StateId state)
    {
        int low = 0, high = _bindings.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int comparison = _bindings[middle].State.Value.CompareTo(state.Value);
            if (comparison < 0) low = middle + 1;
            else if (comparison > 0) high = middle - 1;
            else return middle;
        }
        return -1;
    }

    private static bool Contains(StateId[] states, StateId state) => Array.BinarySearch(states, state, StateOrder.Instance) >= 0;

    private static Setup CreateSetup(StateMachineDefinition definition, ReadOnlySpan<StateBehaviorBinding<TContext>> bindings,
        ReadOnlySpan<StateId> successStates, ReadOnlySpan<StateId> failureStates, BehaviorCompositionLimits limits)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        BindingValidation<TContext>.ValidateCount(bindings.Length, limits);
        var copied = BindingValidation<TContext>.Copy(bindings, definition.StateCount, definition.SignalCount);
        var success = BindingValidation<TContext>.CopyStates(successStates, definition.StateCount, nameof(successStates));
        var failure = BindingValidation<TContext>.CopyStates(failureStates, definition.StateCount, nameof(failureStates));
        foreach (var state in success)
            if (Contains(failure, state)) throw new ArgumentException("A terminal state cannot map to both success and failure.");
        return new Setup(definition, copied, BindingValidation<TContext>.Children(copied), success, failure);
    }

    private readonly struct Setup
    {
        internal Setup(StateMachineDefinition definition, StateBehaviorBinding<TContext>[] bindings,
            Behavior<TContext>[] children, StateId[] success, StateId[] failure)
        { Definition = definition; Bindings = bindings; Children = children; Success = success; Failure = failure; }
        internal StateMachineDefinition Definition { get; }
        internal StateBehaviorBinding<TContext>[] Bindings { get; }
        internal Behavior<TContext>[] Children { get; }
        internal StateId[] Success { get; }
        internal StateId[] Failure { get; }
    }
    private sealed class StateOrder : IComparer<StateId>
    {
        internal static readonly StateOrder Instance = new();
        public int Compare(StateId left, StateId right) => left.Value.CompareTo(right.Value);
    }
}

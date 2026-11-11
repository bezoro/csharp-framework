using Bezoro.BehaviorTrees;
using Bezoro.Statecharts;
using Bezoro.StateMachines;

namespace Bezoro.Behaviors;

/// <summary>Runs bound child behaviors only while their chart states are active.</summary>
/// <remarks>
/// Signals are explicit. Child completion signals are optional, bounded, activation-tagged, and delivered once.
/// No final-state mapping means the machine remains Running. A mapped failure takes precedence over success.
/// </remarks>
public sealed class StatechartBehavior<TContext> : Behavior<TContext>, IStatechartHandler<TContext>
{
    private readonly StatechartRunner<TContext> _runner;
    private readonly IStatechartHandler<TContext>? _handler;
    private readonly StateBehaviorBinding<TContext>[] _bindings;
    private readonly StateId[] _successStates;
    private readonly StateId[] _failureStates;
    private readonly int[] _activeBindings;
    private readonly Completion[] _completions;
    private readonly int _maximumCompletionsPerTick;
    private int _activeCount;
    private int _nextActive;
    private int _completionHead;
    private int _completionCount;
    private int _activeSuccess;
    private int _activeFailure;
    private int _phaseRemaining;
    private bool _phaseHadSuspension;

    /// <summary>Creates a bounded statechart adapter and validates every child and terminal mapping.</summary>
    /// <param name="definition">The shared chart definition.</param>
    /// <param name="handler">Optional guards and lifecycle effects.</param>
    /// <param name="bindings">Child runtimes bound to unique state occurrences.</param>
    /// <param name="successStates">States that complete the machine successfully.</param>
    /// <param name="failureStates">States that complete the machine with failure, taking precedence over success.</param>
    /// <param name="limits">Limits on nested runtime ownership.</param>
    /// <param name="maximumCompletionsPerTick">Maximum completion signals delivered in one Tick, also limited by its shared budget.</param>
    public StatechartBehavior(StatechartDefinition definition, IStatechartHandler<TContext>? handler = null,
        ReadOnlySpan<StateBehaviorBinding<TContext>> bindings = default,
        ReadOnlySpan<StateId> successStates = default, ReadOnlySpan<StateId> failureStates = default,
        BehaviorCompositionLimits limits = default, int maximumCompletionsPerTick = 16)
        : this(CreateSetup(definition, handler, bindings, successStates, failureStates, maximumCompletionsPerTick, limits), limits) { }

    private StatechartBehavior(Setup setup, BehaviorCompositionLimits limits) : base(setup.Children, limits, true)
    {
        _handler = setup.Handler;
        _bindings = setup.Bindings;
        _successStates = setup.Success;
        _failureStates = setup.Failure;
        _activeBindings = setup.Active;
        _completions = setup.Completions;
        _maximumCompletionsPerTick = setup.MaximumCompletions;
        _runner = setup.Runner;
        setup.Routing.Owner = this;
    }

    /// <summary>Gets the shared immutable chart definition.</summary>
    public StatechartDefinition Definition => _runner.Definition;
    /// <summary>Gets the borrowed active state view, valid until the next lifecycle or signal operation.</summary>
    public ReadOnlySpan<StateId> ActiveStates => _runner.ActiveStates;
    /// <summary>Gets the child bindings, ordered by state ID.</summary>
    public ReadOnlySpan<StateBehaviorBinding<TContext>> Bindings => _bindings;
    /// <summary>Gets the maximum number of child-completion signals delivered per Tick.</summary>
    public int MaximumCompletionsPerTick => _maximumCompletionsPerTick;
    /// <summary>Gets the number of queued child completions, including stale entries awaiting bounded removal.</summary>
    public int PendingCompletionCount => _completionCount;
    /// <summary>Gets the reserved completion rows. No rows are allocated when no binding emits a completion signal.</summary>
    public int CompletionCapacity => _completions.Length;
    /// <summary>Tests whether a state is active.</summary>
    public bool IsActive(StateId state) => _runner.IsActive(state);
    /// <summary>Tests whether a state activation is still live.</summary>
    public bool IsActive(StateActivation activation) => CanAcceptSignals && _runner.IsActive(activation);
    /// <summary>Gets the token for an active state.</summary>
    public StateActivation GetActivation(StateId state) => _runner.GetActivation(state);

    /// <summary>Delivers one host signal synchronously, exclusively borrowing the whole composition.</summary>
    public StatechartDispatchResult Dispatch(SignalId signal, ref TContext context)
    {
        BeginSignalOperation();
        try { return DispatchCore(signal, ref context); }
        finally { EndSignalOperation(); }
    }

    private StatechartDispatchResult DispatchCore(SignalId signal, ref TContext context)
    {
        try
        {
            var result = _runner.Dispatch(signal, ref context);
            if (result.ExitedCount != 0) PruneCompletions();
            return result;
        }
        catch (Exception failure)
        {
            // Pure guard failures preserve the chart. Lifecycle failures invalidate the component.
            if (_runner.IsFaulted)
            {
                var cleanup = FaultCleanup(ref context);
                if (cleanup is not null) throw new AggregateException("Signal processing and cleanup both failed.", failure, cleanup);
            }
            throw;
        }
    }

    /// <summary>Delivers a signal only while its originating activation is still live and this component is Running.</summary>
    /// <returns>False for an old, foreign, default, stopped, or terminal activation.</returns>
    public bool TryDispatch(StateActivation activation, SignalId signal, ref TContext context)
    {
        if (IsExecutingOnCurrentThread && !IsCurrentSignal(activation)) return false;
        BeginSignalOperation(requireRunning: false);
        try
        {
            if (!IsCurrentSignal(activation)) return false;
            DispatchCore(signal, ref context);
            return true;
        }
        finally { EndSignalOperation(); }
    }

    private bool IsCurrentSignal(StateActivation activation) =>
        CanAcceptSignals && _runner.IsRunning && _runner.IsActive(activation);

    /// <inheritdoc />
    protected override void OnStart(ref TContext context)
    {
        _activeCount = _nextActive = _completionHead = _completionCount = _activeSuccess = _activeFailure = _phaseRemaining = 0;
        _phaseHadSuspension = false;
        if (_runner.IsFaulted) _runner.Reset(ref context);
        _runner.Start(ref context);
    }

    /// <inheritdoc />
    protected override BehaviorStatus OnTick(ref TContext context, ref BehaviorBudget budget)
    {
        BehaviorStatus terminal = TerminalStatus;
        if (terminal != BehaviorStatus.Running) return terminal;
        int delivered = DeliverCompletions(ref context, ref budget, 0);
        terminal = TerminalStatus;
        if (terminal != BehaviorStatus.Running) return terminal;
        // Complete one round-robin phase across updates before reporting a normal Running yield.
        if (_phaseRemaining == 0)
        { _phaseRemaining = _activeCount; _phaseHadSuspension = false; }
        while (_phaseRemaining > 0 && _activeCount > 0 && budget.Remaining > 0)
        {
            _phaseRemaining--;
            if (_nextActive >= _activeCount) _nextActive = 0;
            int position = _nextActive;
            int bindingIndex = _activeBindings[position];
            var binding = _bindings[bindingIndex];
            var activation = _runner.GetActivation(binding.State);
            BehaviorStatus result = TickChild(binding.Child, ref context, ref budget);
            _phaseHadSuspension |= binding.Child.IsBudgetSuspended;
            if (result == BehaviorStatus.Running)
                _nextActive = (position + 1) % _activeCount;
            else
            {
                RemoveActiveAt(position);
                SignalId? signal = result == BehaviorStatus.Success ? binding.SuccessSignal : binding.FailureSignal;
                if (signal.HasValue) Enqueue(binding.State, activation, signal.Value);
            }
        }
        DeliverCompletions(ref context, ref budget, delivered);
        IsBudgetSuspended = budget.Remaining == 0 &&
            (_phaseHadSuspension || (_phaseRemaining > 0 && _activeCount > 0) || _completionCount > 0);
        return TerminalStatus;
    }

    /// <inheritdoc />
    protected override void OnStop(ref TContext context)
    {
        try { _runner.Reset(ref context); }
        finally
        {
            // Cleanup removes each activation before invoking its callback. Once empty, a failed
            // cleanup can be reset without reentering application exit effects.
            if (_runner.IsFaulted && !_runner.IsRunning && _runner.ActiveStates.Length == 0)
                _runner.Reset(ref context);
            _activeCount = _nextActive = _completionHead = _completionCount = _activeSuccess = _activeFailure = _phaseRemaining = 0;
            _phaseHadSuspension = false;
        }
    }

    /// <inheritdoc />
    protected override bool IsChildScopeLive(int childIndex) => _runner.IsRunning &&
        _runner.TryGetActivation(_bindings[childIndex].State, out var activation) && _runner.IsActive(activation);

    bool IStatechartHandler<TContext>.EvaluateGuard(int predicateId, StatechartView configuration, in TContext context) =>
        (_handler ?? throw new InvalidOperationException("The chart guard has no host evaluator."))
            .EvaluateGuard(predicateId, configuration, in context);

    void IStatechartHandler<TContext>.OnEnter(StateId state, StateActivation activation, ref TContext context)
    {
        if (Contains(_successStates, state)) _activeSuccess++;
        if (Contains(_failureStates, state)) _activeFailure++;
        _handler?.OnEnter(state, activation, ref context);
        int index = FindBinding(state);
        if (index < 0) return;
        _phaseHadSuspension = false;
        _activeBindings[_activeCount++] = index;
        _phaseRemaining = _activeCount;
        StartChild(_bindings[index].Child, ref context);
    }

    void IStatechartHandler<TContext>.OnExit(StateId state, StateActivation activation, ref TContext context)
    {
        if (Contains(_successStates, state)) _activeSuccess--;
        if (Contains(_failureStates, state)) _activeFailure--;
        int index = FindBinding(state);
        Exception? failure = null;
        if (index >= 0)
        {
            _phaseHadSuspension = false;
            RemoveActiveBinding(index);
            _phaseRemaining = _activeCount;
            try { StopChild(_bindings[index].Child, ref context); }
            catch (Exception error) { failure = error; }
        }
        try { _handler?.OnExit(state, activation, ref context); }
        catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    void IStatechartHandler<TContext>.OnTransition(int transitionIndex, in ChartTransition transition, ref TContext context) =>
        _handler?.OnTransition(transitionIndex, in transition, ref context);

    private BehaviorStatus TerminalStatus => _activeFailure != 0 ? BehaviorStatus.Failure :
        _activeSuccess != 0 ? BehaviorStatus.Success : BehaviorStatus.Running;

    private int DeliverCompletions(ref TContext context, ref BehaviorBudget budget, int delivered)
    {
        while (_completionCount > 0)
        {
            Completion completion = _completions[_completionHead];
            if (!IsCurrent(completion)) { Dequeue(); continue; }
            if (delivered >= _maximumCompletionsPerTick) break;
            if (!budget.TryConsume()) break;
            Dequeue();
            var result = _runner.Dispatch(completion.Signal, ref context);
            if (result.ExitedCount != 0) PruneCompletions();
            delivered++;
            if (TerminalStatus != BehaviorStatus.Running) break;
        }
        return delivered;
    }

    private void Enqueue(StateId state, StateActivation activation, SignalId signal)
    {
        if (_completionCount == _completions.Length)
            throw new InvalidOperationException("The bounded child completion queue is full.");
        int tail = CompletionIndex(_completionCount);
        _completions[tail] = new Completion(activation, state, signal);
        _completionCount++;
    }

    private void Dequeue()
    {
        _completions[_completionHead] = default;
        _completionHead = (_completionHead + 1) % _completions.Length;
        _completionCount--;
    }

    private void RemoveActiveBinding(int bindingIndex)
    {
        for (int i = 0; i < _activeCount; i++)
            if (_activeBindings[i] == bindingIndex) { RemoveActiveAt(i); return; }
    }

    private void RemoveActiveAt(int index)
    {
        int last = --_activeCount;
        if (index < last) Array.Copy(_activeBindings, index + 1, _activeBindings, index, last - index);
        _activeBindings[last] = 0;
        if (index < _nextActive) _nextActive--;
        if (_activeCount == 0 || _nextActive >= _activeCount) _nextActive = 0;
    }

    private void PruneCompletions()
    {
        int original = _completionCount;
        int kept = 0;
        for (int i = 0; i < original; i++)
        {
            Completion completion = _completions[CompletionIndex(i)];
            if (IsCurrent(completion))
            { _completions[CompletionIndex(kept)] = completion; kept++; }
        }
        for (int i = kept; i < original; i++) _completions[CompletionIndex(i)] = default;
        _completionCount = kept;
    }

    private int CompletionIndex(int offset)
    {
        int toEnd = _completions.Length - _completionHead;
        return offset < toEnd ? _completionHead + offset : offset - toEnd;
    }

    private bool IsCurrent(Completion completion) => _runner.IsRunning &&
        _runner.TryGetActivation(completion.State, out var current) && current == completion.Activation;

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

    private static Setup CreateSetup(StatechartDefinition definition, IStatechartHandler<TContext>? handler,
        ReadOnlySpan<StateBehaviorBinding<TContext>> bindings, ReadOnlySpan<StateId> success,
        ReadOnlySpan<StateId> failure, int maximumCompletions, BehaviorCompositionLimits limits)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        if (maximumCompletions <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCompletions));
        BindingValidation<TContext>.ValidateCount(bindings.Length, limits);
        var copied = BindingValidation<TContext>.Copy(bindings, definition.StateCount, definition.SignalCount);
        var successCopy = BindingValidation<TContext>.CopyStates(success, definition.StateCount, nameof(success));
        var failureCopy = BindingValidation<TContext>.CopyStates(failure, definition.StateCount, nameof(failure));
        foreach (var state in successCopy)
            if (Contains(failureCopy, state)) throw new ArgumentException("A terminal state cannot map to both success and failure.");
        int activeCapacity = Math.Min(copied.Length, definition.MaximumActiveStates);
        int completionCapacity = 0;
        foreach (var binding in copied)
            if (binding.SuccessSignal.HasValue || binding.FailureSignal.HasValue) completionCapacity++;
        completionCapacity = Math.Min(completionCapacity, activeCapacity);
        var routing = new RoutingHandler();
        var runner = new StatechartRunner<TContext>(definition, routing);
        return new Setup(runner, routing, handler, copied, BindingValidation<TContext>.Children(copied), successCopy, failureCopy,
            activeCapacity == 0 ? Array.Empty<int>() : new int[activeCapacity],
            completionCapacity == 0 ? Array.Empty<Completion>() : new Completion[completionCapacity], maximumCompletions);
    }

    private readonly struct Completion
    {
        internal Completion(StateActivation activation, StateId state, SignalId signal)
        { Activation = activation; State = state; Signal = signal; }
        internal StateActivation Activation { get; }
        internal StateId State { get; }
        internal SignalId Signal { get; }
    }
    private readonly struct Setup
    {
        internal Setup(StatechartRunner<TContext> runner, RoutingHandler routing, IStatechartHandler<TContext>? handler,
            StateBehaviorBinding<TContext>[] bindings, Behavior<TContext>[] children, StateId[] success,
            StateId[] failure, int[] active, Completion[] completions, int maximumCompletions)
        { Runner = runner; Routing = routing; Handler = handler; Bindings = bindings; Children = children; Success = success;
            Failure = failure; Active = active; Completions = completions; MaximumCompletions = maximumCompletions; }
        internal StatechartRunner<TContext> Runner { get; }
        internal RoutingHandler Routing { get; }
        internal IStatechartHandler<TContext>? Handler { get; }
        internal StateBehaviorBinding<TContext>[] Bindings { get; }
        internal Behavior<TContext>[] Children { get; }
        internal StateId[] Success { get; }
        internal StateId[] Failure { get; }
        internal int[] Active { get; }
        internal Completion[] Completions { get; }
        internal int MaximumCompletions { get; }
    }
    private sealed class StateOrder : IComparer<StateId>
    {
        internal static readonly StateOrder Instance = new();
        public int Compare(StateId left, StateId right) => left.Value.CompareTo(right.Value);
    }
    private sealed class RoutingHandler : IStatechartHandler<TContext>
    {
        internal StatechartBehavior<TContext> Owner { private get; set; } = null!;
        public bool EvaluateGuard(int predicateId, StatechartView configuration, in TContext context) =>
            ((IStatechartHandler<TContext>)Owner).EvaluateGuard(predicateId, configuration, in context);
        public void OnEnter(StateId state, StateActivation activation, ref TContext context) =>
            ((IStatechartHandler<TContext>)Owner).OnEnter(state, activation, ref context);
        public void OnExit(StateId state, StateActivation activation, ref TContext context) =>
            ((IStatechartHandler<TContext>)Owner).OnExit(state, activation, ref context);
        public void OnTransition(int transitionIndex, in ChartTransition transition, ref TContext context) =>
            ((IStatechartHandler<TContext>)Owner).OnTransition(transitionIndex, in transition, ref context);
    }
}

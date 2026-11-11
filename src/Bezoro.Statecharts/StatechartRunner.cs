using System.Threading;
using Bezoro.Conditions;
using Bezoro.StateMachines;

namespace Bezoro.Statecharts;

/// <summary>Owns one active configuration and bounded event workspace for a shared chart definition.</summary>
/// <remarks>
/// Only active sources are examined on Dispatch. There is no polling, timer, worker, or event queue.
/// Independent runners can execute concurrently. Concurrent or reentrant lifecycle operations on one runner are rejected
/// before mutation. State inspection and borrowed views belong to the execution owner. Callbacks are synchronous.
/// </remarks>
public sealed class StatechartRunner<TContext>
{
    private readonly IStatechartHandler<TContext>? _handler;
    private readonly long _owner;
    private readonly StateId[] _active;
    private readonly ulong[] _serials;
    private readonly int[] _candidates;
    private readonly int[] _selected;
    private readonly StateId[] _exitPlan;
    private readonly StateId[] _entryPlan;
    private readonly int[] _regionStack;
    private readonly StateId[] _targetPath;
    private readonly bool[] _guardScratch;
    private int _activeCount;
    private int _pendingExitCount;
    private ulong _lastSerial;
    private int _busy;

    /// <summary>Allocates execution storage from peak simultaneous activity, with no storage growth afterward.</summary>
    public StatechartRunner(StatechartDefinition definition, IStatechartHandler<TContext>? handler = null)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _handler = handler;
        _owner = ActivationOwners.Next();
        int peak = definition.MaximumActiveStates;
        _active = new StateId[peak];
        _serials = new ulong[peak];
        int candidateCapacity = Math.Min(peak, definition.TransitionSourceCount);
        _candidates = candidateCapacity == 0 ? Array.Empty<int>() : new int[candidateCapacity];
        _selected = candidateCapacity == 0 ? Array.Empty<int>() : new int[candidateCapacity];
        bool hasTransitions = definition.HasConfigurationTransitions;
        _exitPlan = hasTransitions ? new StateId[peak] : Array.Empty<StateId>();
        _entryPlan = hasTransitions ? new StateId[peak] : Array.Empty<StateId>();
        _regionStack = hasTransitions ? new int[peak] : Array.Empty<int>();
        _targetPath = hasTransitions ? new StateId[definition.MaximumDepth] : Array.Empty<StateId>();
        _guardScratch = definition.GuardStackSize == 0 ? Array.Empty<bool>() : new bool[definition.GuardStackSize];
    }

    /// <summary>Gets the shared immutable definition.</summary>
    public StatechartDefinition Definition { get; }
    /// <summary>Gets a borrowed parent-before-descendant view, invalidated by the next lifecycle operation.</summary>
    public ReadOnlySpan<StateId> ActiveStates => _active.AsSpan(0, _activeCount);
    /// <summary>Gets whether initial entry completed and signals can be dispatched.</summary>
    public bool IsRunning { get; private set; }
    /// <summary>Gets whether a lifecycle failure requires an explicit Reset before Start.</summary>
    public bool IsFaulted { get; private set; }

    /// <summary>Starts the initial configuration. Starting an already-running or faulted runner throws.</summary>
    public void Start(ref TContext context)
    {
        EnterOperation();
        try { StartCore(ref context); }
        finally { ExitOperation(); }
    }

    private void StartCore(ref TContext context)
    {
        if (IsRunning) throw new InvalidOperationException("The chart is already running.");
        if (IsFaulted) throw new InvalidOperationException("Reset the faulted chart before starting it.");
        ReserveActivations(Definition.InitialStates.Length);
        IsRunning = true;
        try
        {
            foreach (StateId state in Definition.InitialStates) Enter(state, ref context);
        }
        catch (Exception failure) { FailAndCleanup(failure, ref context); throw; }
    }

    /// <summary>Stops and invalidates all activations, attempting every exit even if an exit callback fails.</summary>
    /// <remarks>Stopping an inactive runner is idempotent. A callback failure leaves it stopped and faulted.</remarks>
    public void Stop(ref TContext context)
    {
        EnterOperation();
        try { StopCore(ref context); }
        finally { ExitOperation(); }
    }

    private void StopCore(ref TContext context)
    {
        IsRunning = false;
        List<Exception>? failures = null;
        Cleanup(ref context, ref failures);
        if (failures is not null)
        {
            IsFaulted = true;
            ThrowFailures(failures);
        }
    }

    /// <summary>Stops any active configuration and clears a prior fault when cleanup succeeds.</summary>
    public void Reset(ref TContext context)
    {
        EnterOperation();
        try { StopCore(ref context); IsFaulted = false; }
        finally { ExitOperation(); }
    }

    /// <summary>Evaluates one explicit signal against active sources and applies nonconflicting transitions.</summary>
    /// <remarks>
    /// Guards observe the unchanged prior configuration. A guard exception preserves configuration and running status.
    /// Once lifecycle effects begin, an exception faults and stops the runner; effects are not rolled back.
    /// </remarks>
    public StatechartDispatchResult Dispatch(SignalId signal, ref TContext context)
    {
        EnterOperation();
        try { return DispatchCore(signal, ref context); }
        finally { ExitOperation(); }
    }

    private StatechartDispatchResult DispatchCore(SignalId signal, ref TContext context)
    {
        if (!IsRunning) throw new InvalidOperationException("Start the chart before dispatching signals.");
        if (signal.Value >= Definition.SignalCount) throw new ArgumentOutOfRangeException(nameof(signal));
        bool changing = false;
        try
        {
            int candidateCount = 0;
            foreach (StateId state in ActiveStates)
            {
                Definition.GetSignalRange(state, signal, out int first, out int end);
                if (first < end) PushSourceHead(first, ref candidateCount);
            }
            int selectedCount = 0;
            var evaluator = new GuardEvaluator(this);
            while (candidateCount > 0)
            {
                int ordered = PopSourceHead(ref candidateCount);
                int candidate = Definition.OrderedTransition(ordered);
                var transition = Definition.Transitions[candidate];
                bool suppressed = false;
                for (int i = 0; i < selectedCount; i++)
                    if (Definition.SuppressesSource(_selected[i], transition.Source)) { suppressed = true; break; }
                if (suppressed) continue;
                if (transition.Guard is not null &&
                    !transition.Guard.Evaluate<TContext, GuardEvaluator>(in context, ref evaluator, _guardScratch))
                {
                    if (Definition.HasFollowingGuard(ordered)) PushSourceHead(ordered + 1, ref candidateCount);
                    continue;
                }
                bool conflict = false;
                for (int i = 0; i < selectedCount; i++)
                    if (Definition.Conflicts(candidate, _selected[i])) { conflict = true; break; }
                if (!conflict) _selected[selectedCount++] = candidate;
            }
            if (selectedCount == 0) return default;

            // Reuse candidate storage for configuration-changing domains only. Internal
            // effects therefore reserve and traverse no lifecycle plan when none is needed.
            int domainCount = 0;
            for (int i = 0; i < selectedCount; i++)
            {
                int value = _selected[i];
                if (Definition.Domain(value) < 0) continue;
                int j = domainCount++;
                while (j > 0 && Definition.DomainStart(_candidates[j - 1]) > Definition.DomainStart(value))
                { _candidates[j] = _candidates[j - 1]; j--; }
                _candidates[j] = value;
            }
            int exitCount = 0;
            if (domainCount > 0)
                foreach (StateId state in ActiveStates)
                    for (int i = 0; i < domainCount; i++)
                        if (Definition.IsInDomain(state, Definition.Domain(_candidates[i])))
                        { _exitPlan[exitCount++] = state; break; }
            int entryCount = 0;
            for (int i = 0; i < domainCount; i++)
                entryCount = Definition.AppendEntries(_candidates[i], _entryPlan, entryCount, _regionStack, _targetPath);
            ReserveActivations(entryCount);
            changing = true;
            _pendingExitCount = exitCount;
            for (int i = exitCount - 1; i >= 0; i--)
            {
                Exit(_exitPlan[i], ref context);
                _pendingExitCount = i;
            }
            _pendingExitCount = 0;
            for (int i = 0; i < selectedCount; i++)
            {
                int transition = _selected[i];
                _handler?.OnTransition(transition, in Definition.Transitions[transition], ref context);
            }
            for (int i = 0; i < entryCount; i++) Enter(_entryPlan[i], ref context);
            return new StatechartDispatchResult(selectedCount, exitCount, entryCount);
        }
        catch (Exception failure)
        {
            if (changing) FailAndCleanup(failure, ref context);
            throw;
        }
        finally { _pendingExitCount = 0; }
    }

    /// <summary>Tests whether the supplied numeric state is currently active.</summary>
    public bool IsActive(StateId state) => Definition.FindActive(ActiveStates, state) >= 0;

    /// <summary>Gets an active state's current entry identity, throwing when the state is inactive or outside this definition.</summary>
    public StateActivation GetActivation(StateId state) => TryGetActivation(state, out var activation)
        ? activation : throw new ArgumentException("The state is not active in this runner.", nameof(state));

    /// <summary>Gets a state's current activation, or returns false and a default token when inactive.</summary>
    public bool TryGetActivation(StateId state, out StateActivation activation)
    {
        int index = Definition.FindActive(ActiveStates, state);
        activation = index < 0 ? default : new StateActivation(_owner, _serials[index]);
        return index >= 0;
    }

    /// <summary>Tests whether a token issued by this runner remains live in the running chart.</summary>
    /// <remarks>
    /// Stop and lifecycle failure revoke all tokens before exit callbacks. Dispatch also revokes tokens
    /// for its complete exit plan before callbacks, preserving unaffected and newly entered activations.
    /// </remarks>
    public bool IsActive(StateActivation activation)
    {
        if (!IsRunning || activation.Owner != _owner || activation.Serial == 0) return false;
        for (int i = 0; i < _activeCount; i++)
            if (_serials[i] == activation.Serial)
                return _pendingExitCount == 0 ||
                    Definition.FindActive(_exitPlan.AsSpan(0, _pendingExitCount), _active[i]) < 0;
        return false;
    }

    private int CompareHeads(int first, int second) =>
        Definition.CompareCandidates(Definition.OrderedTransition(first), Definition.OrderedTransition(second));

    private void PushSourceHead(int ordered, ref int count)
    {
        int index = count++;
        while (index > 0)
        {
            int parent = (index - 1) >> 1;
            if (CompareHeads(ordered, _candidates[parent]) >= 0) break;
            _candidates[index] = _candidates[parent];
            index = parent;
        }
        _candidates[index] = ordered;
    }

    private int PopSourceHead(ref int count)
    {
        int first = _candidates[0];
        int last = _candidates[--count];
        int index = 0;
        // An index with a child is below count / 2. This also avoids index*2 overflow.
        while (count > 1 && index <= (count - 2) / 2)
        {
            int child = index * 2 + 1;
            if (child + 1 < count && CompareHeads(_candidates[child + 1], _candidates[child]) < 0) child++;
            if (CompareHeads(last, _candidates[child]) <= 0) break;
            _candidates[index] = _candidates[child];
            index = child;
        }
        if (count != 0) _candidates[index] = last;
        return first;
    }

    private void Enter(StateId state, ref TContext context)
    {
        int index = _activeCount;
        int rank = Definition.Rank(state);
        while (index > 0 && Definition.Rank(_active[index - 1]) > rank)
        {
            _active[index] = _active[index - 1];
            _serials[index] = _serials[index - 1];
            index--;
        }
        _active[index] = state;
        _serials[index] = ++_lastSerial;
        _activeCount++;
        _handler?.OnEnter(state, new StateActivation(_owner, _lastSerial), ref context);
    }

    private void Exit(StateId state, ref TContext context)
    {
        int index = Definition.FindActive(ActiveStates, state);
        var token = new StateActivation(_owner, _serials[index]);
        int following = _activeCount - index - 1;
        if (following > 0)
        {
            Array.Copy(_active, index + 1, _active, index, following);
            Array.Copy(_serials, index + 1, _serials, index, following);
        }
        _active[--_activeCount] = default;
        _serials[_activeCount] = 0;
        _handler?.OnExit(state, token, ref context);
    }

    private void Cleanup(ref TContext context, ref List<Exception>? failures)
    {
        while (_activeCount != 0)
        {
            StateId state = _active[_activeCount - 1];
            try { Exit(state, ref context); }
            catch (Exception failure) { (failures ??= new List<Exception>()).Add(failure); }
        }
    }

    private void FailAndCleanup(Exception failure, ref TContext context)
    {
        IsRunning = false;
        IsFaulted = true;
        List<Exception>? cleanupFailures = null;
        Cleanup(ref context, ref cleanupFailures);
        if (cleanupFailures is not null)
        {
            cleanupFailures.Insert(0, failure);
            throw new AggregateException("Statechart execution and cleanup failed.", cleanupFailures);
        }
    }
    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException("Statechart cleanup failed.", failures);
    }
    private void ReserveActivations(int count)
    {
        if (_lastSerial > ulong.MaxValue - (ulong)count)
            throw new InvalidOperationException("State activation identities are exhausted; create a new runner.");
    }
    private void EnterOperation()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("Concurrent or reentrant statechart operations are not supported.");
    }
    private void ExitOperation() => Volatile.Write(ref _busy, 0);

    private readonly struct GuardEvaluator : IConditionEvaluator<TContext>
    {
        private readonly StatechartRunner<TContext> _runner;
        internal GuardEvaluator(StatechartRunner<TContext> runner) => _runner = runner;
        public bool Evaluate(int predicateId, in TContext context) => _runner._handler?.EvaluateGuard(predicateId,
            new StatechartView(_runner.Definition, _runner.ActiveStates), in context)
            ?? throw new InvalidOperationException("The reached guard predicate has no evaluator.");
    }
}

internal static class ActivationOwners
{
    private static long _last;
    internal static long Next()
    {
        while (true)
        {
            long current = Interlocked.Read(ref _last);
            if (current == long.MaxValue) throw new InvalidOperationException("Statechart runner identities are exhausted.");
            if (Interlocked.CompareExchange(ref _last, current + 1, current) == current) return current + 1;
        }
    }
}

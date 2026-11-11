using System.Runtime.CompilerServices;
using Bezoro.BehaviorTrees;

namespace Bezoro.Behaviors;

/// <summary>Implements lifecycle, exclusive child ownership, and bounded composition for behavior adapters.</summary>
/// <remarks>
/// Definitions can be shared and independent graphs can run concurrently. An atomic graph reservation rejects
/// overlapping or reentrant lifecycle/signal operations before mutation, including calls to different descendants.
/// Contexts, handler state and inspection views remain exclusively borrowed by the executing owner.
/// Derived implementations must keep their configured child graph immutable after construction.
/// </remarks>
public abstract class Behavior<TContext> : IBehavior<TContext>
{
    private readonly Behavior<TContext>[] _children;
    private Behavior<TContext>? _parent;
    private int _parentChildIndex = -1;
    private Behavior<TContext>? _activeHead;
    private Behavior<TContext>? _activePrevious;
    private Behavior<TContext>? _activeNext;
    private bool _startedByParent;
    private bool _executing;
    private int _parentPermitThread;
    private int _graphOperationThread;
    private Behavior<TContext>? _operationRoot;
    private bool _ownsGraphOperation;
    private bool _cleanupAttempted;
    private BehaviorStatus _status;

    /// <summary>Creates a leaf with no configured child runtimes.</summary>
    protected Behavior(BehaviorCompositionLimits limits = default)
        : this(Array.Empty<Behavior<TContext>>(), limits, true) { }

    /// <summary>Copies the child references after validating their graph and configured limits.</summary>
    protected Behavior(Behavior<TContext>[] children, BehaviorCompositionLimits limits)
        : this(children, limits, false) { }

    internal Behavior(Behavior<TContext>[] children, BehaviorCompositionLimits limits, bool adoptStorage)
    {
        if (children is null) throw new ArgumentNullException(nameof(children));
        if (children.Length >= limits.ComponentLimit)
            throw new ArgumentOutOfRangeException(nameof(limits), "The direct children exceed the component limit.");
        int depth = 1;
        long count = 1;
        HashSet<Behavior<TContext>>? unique = children.Length > 1
            ? new HashSet<Behavior<TContext>>(ReferenceComparer.Instance) : null;
        foreach (var child in children)
        {
            if (child is null) throw new ArgumentException("A child behavior cannot be null.", nameof(children));
            if (ReferenceEquals(this, child) || child._parent is not null || child.Status != BehaviorStatus.Inactive ||
                (unique is not null && !unique.Add(child)))
                throw new ArgumentException("Each inactive child runtime must be owned by exactly one occurrence.", nameof(children));
            depth = Math.Max(depth, checked(child.CompositionDepth + 1));
            count += child.ComponentCount;
        }
        if (depth > limits.DepthLimit) throw new ArgumentOutOfRangeException(nameof(limits), "The composition exceeds its depth limit.");
        if (count > limits.ComponentLimit) throw new ArgumentOutOfRangeException(nameof(limits), "The composition exceeds its component limit.");
        _children = children.Length == 0 ? Array.Empty<Behavior<TContext>>() :
            adoptStorage ? children : CopyChildren(children);
        CompositionDepth = depth;
        ComponentCount = (int)count;
        // Reserve each inactive root before adopting it. This excludes concurrent parents
        // and execution without retaining a lock or allocating runtime ownership handles.
        _graphOperationThread = -1;
        int claimed = 0;
        try
        {
            foreach (var child in _children)
            {
                if (Interlocked.CompareExchange(ref child._graphOperationThread, -1, 0) != 0)
                    throw new ArgumentException("A child runtime is already executing or being adopted.", nameof(children));
                claimed++;
                if (child._parent is not null || child.Status != BehaviorStatus.Inactive)
                    throw new ArgumentException("Each inactive child runtime must be owned by exactly one occurrence.", nameof(children));
            }
            // Publish parent links only after every reservation succeeds.
            for (int i = 0; i < _children.Length; i++)
            { _children[i]._parent = this; _children[i]._parentChildIndex = i; }
        }
        finally
        {
            for (int i = 0; i < claimed; i++) Volatile.Write(ref _children[i]._graphOperationThread, 0);
            Volatile.Write(ref _graphOperationThread, 0);
        }
    }

    /// <inheritdoc />
    public BehaviorStatus Status => _status;
    /// <inheritdoc />
    public bool IsBudgetSuspended { get; protected set; }
    /// <summary>Gets the depth of this immutable runtime composition, including this component.</summary>
    public int CompositionDepth { get; }
    /// <summary>Gets the number of runtime components, including this component.</summary>
    public int ComponentCount { get; }
    /// <summary>Gets the configured children. They can be inspected but are controlled by this parent.</summary>
    public ReadOnlySpan<Behavior<TContext>> Children => _children;

    /// <inheritdoc />
    public void Start(ref TContext context)
    {
        EnterOperation(requireRunning: false);
        try
        {
            if (_status != BehaviorStatus.Inactive)
                throw new InvalidOperationException("Stop a behavior before starting it again.");
            _status = BehaviorStatus.Running;
            IsBudgetSuspended = false;
            _cleanupAttempted = false;
            try { OnStart(ref context); }
            catch (Exception failure)
            {
                var cleanup = FaultCleanup(ref context);
                if (cleanup is not null) throw new AggregateException("Behavior startup and cleanup both failed.", failure, cleanup);
                throw;
            }
        }
        finally { EndOperation(); }
    }

    /// <inheritdoc />
    public BehaviorStatus Tick(ref TContext context, ref BehaviorBudget budget)
    {
        EnterOperation(requireRunning: false);
        try
        {
            if (_status == BehaviorStatus.Inactive) throw new InvalidOperationException("Start the behavior before ticking it.");
            if (_status == BehaviorStatus.Faulted) throw new InvalidOperationException("Stop the faulted behavior before reuse.");
            if (_status != BehaviorStatus.Running) return _status;
            try
            {
                IsBudgetSuspended = false;
                var result = OnTick(ref context, ref budget);
                if (result is not (BehaviorStatus.Running or BehaviorStatus.Success or BehaviorStatus.Failure))
                    throw new InvalidOperationException("A behavior tick must return Running, Success, or Failure.");
                _status = result;
                if (result != BehaviorStatus.Running)
                {
                    IsBudgetSuspended = false;
                    var cleanup = Cleanup(ref context);
                    if (cleanup is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup).Throw();
                }
                return result;
            }
            catch (Exception failure)
            {
                var cleanup = FaultCleanup(ref context);
                if (cleanup is not null) throw new AggregateException("Behavior execution and cleanup both failed.", failure, cleanup);
                throw;
            }
        }
        finally { EndOperation(); }
    }

    /// <inheritdoc />
    public void Stop(ref TContext context)
    {
        EnterOperation(requireRunning: false);
        try
        {
            if (_status == BehaviorStatus.Inactive) return;
            _status = BehaviorStatus.Inactive;
            Exception? failure = Cleanup(ref context);
            IsBudgetSuspended = false;
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { EndOperation(); }
    }

    /// <summary>Initializes this component. Derived types can start only their direct configured children.</summary>
    protected abstract void OnStart(ref TContext context);
    /// <summary>Advances this component while preserving the shared work budget.</summary>
    protected abstract BehaviorStatus OnTick(ref TContext context, ref BehaviorBudget budget);
    /// <summary>Releases component-specific activity. Remaining started children are stopped automatically.</summary>
    protected abstract void OnStop(ref TContext context);

    /// <summary>Tests whether the owning occurrence still accepts results from a configured child.</summary>
    /// <remarks>Override when a state/action scope can be revoked before its child reaches a physical Stop callback.</remarks>
    protected virtual bool IsChildScopeLive(int childIndex) => true;

    /// <summary>Starts a direct child and records it before invoking user code.</summary>
    protected void StartChild(Behavior<TContext> child, ref TContext context)
    {
        ValidateChild(child);
        if (_cleanupAttempted) throw new InvalidOperationException("Cleanup cannot start a child behavior.");
        if (child._startedByParent) throw new InvalidOperationException("The child is already started.");
        child._startedByParent = true;
        child._activeNext = _activeHead;
        if (_activeHead is not null) _activeHead._activePrevious = child;
        _activeHead = child;
        child._parentPermitThread = Environment.CurrentManagedThreadId;
        try { child.Start(ref context); }
        finally { child._parentPermitThread = 0; }
    }

    /// <summary>Advances a direct child that this parent started.</summary>
    protected BehaviorStatus TickChild(Behavior<TContext> child, ref TContext context, ref BehaviorBudget budget)
    {
        ValidateChild(child);
        if (_cleanupAttempted) throw new InvalidOperationException("Cleanup cannot tick a child behavior.");
        if (!child._startedByParent) throw new InvalidOperationException("The child has not been started.");
        child._parentPermitThread = Environment.CurrentManagedThreadId;
        try { return child.Tick(ref context, ref budget); }
        finally { child._parentPermitThread = 0; }
    }

    /// <summary>Stops a direct started child, invalidating its parent tracking before callbacks.</summary>
    protected void StopChild(Behavior<TContext> child, ref TContext context)
    {
        ValidateChild(child);
        if (!child._startedByParent) return;
        child._startedByParent = false;
        if (child._activePrevious is not null) child._activePrevious._activeNext = child._activeNext;
        else _activeHead = child._activeNext;
        if (child._activeNext is not null) child._activeNext._activePrevious = child._activePrevious;
        child._activePrevious = null;
        child._activeNext = null;
        child._parentPermitThread = Environment.CurrentManagedThreadId;
        try { child.Stop(ref context); }
        finally { child._parentPermitThread = 0; }
    }

    /// <summary>Exclusively borrows this composition for one synchronous signal operation.</summary>
    protected void BeginSignalOperation() => BeginSignalOperation(requireRunning: true);

    /// <summary>Exclusively borrows the composition, optionally permitting read-only rejection of inactive activations.</summary>
    private protected void BeginSignalOperation(bool requireRunning) =>
        EnterOperation(requireRunning, allowOwnedSignal: true);
    /// <summary>Ends a signal operation.</summary>
    protected void EndSignalOperation() => EndOperation();
    /// <summary>Gets whether this component and every owning ancestor still accept execution results.</summary>
    protected bool CanAcceptSignals
    {
        get
        {
            if (_status != BehaviorStatus.Running) return false;
            var child = this;
            for (var parent = _parent; parent is not null; parent = parent._parent)
            {
                if (parent._status != BehaviorStatus.Running || !parent.IsChildScopeLive(child._parentChildIndex)) return false;
                child = parent;
            }
            return true;
        }
    }

    /// <summary>Gets whether this thread already owns the graph, allowing read-only stale-result rejection inside callbacks.</summary>
    private protected bool IsExecutingOnCurrentThread
    {
        get
        {
            var root = this;
            while (root._parent is not null) root = root._parent;
            return Volatile.Read(ref root._graphOperationThread) == Environment.CurrentManagedThreadId;
        }
    }
    /// <summary>Stops owned activity after a failing signal operation and marks this component faulted.</summary>
    protected Exception? FaultCleanup(ref TContext context)
    {
        _status = BehaviorStatus.Faulted;
        IsBudgetSuspended = false;
        return Cleanup(ref context);
    }

    private Exception? Cleanup(ref TContext context)
    {
        Exception? failure = null;
        if (!_cleanupAttempted)
        {
            _cleanupAttempted = true;
            try { OnStop(ref context); }
            catch (Exception error) { failure = error; }
        }
        return StopStartedChildren(ref context, failure);
    }

    private Exception? StopStartedChildren(ref TContext context, Exception? first)
    {
        while (_activeHead is not null)
        {
            var child = _activeHead;
            try { StopChild(child, ref context); }
            catch (Exception error) { first = first is null ? error : new AggregateException(first, error); }
        }
        return first;
    }

    private static Behavior<TContext>[] CopyChildren(Behavior<TContext>[] children)
    {
        // A protected caller can pass a covariant DerivedBehavior[]. Copy into an exact
        // base-element array before exposing a span or retaining the graph storage.
        var result = new Behavior<TContext>[children.Length];
        Array.Copy(children, result, children.Length);
        return result;
    }

    private void ValidateChild(Behavior<TContext> child)
    {
        if (!ReferenceEquals(child._parent, this)) throw new ArgumentException("The behavior is not a direct child.", nameof(child));
        if (!_executing) throw new InvalidOperationException("Child lifecycle requires an executing parent.");
    }

    private void EnterOperation(bool requireRunning, bool allowOwnedSignal = false)
    {
        int thread = Environment.CurrentManagedThreadId;
        bool nested = _parent is not null && _parentPermitThread == thread;
        if (_parent is not null && !nested && !allowOwnedSignal)
            throw new InvalidOperationException("An owned child is controlled through its parent.");
        var root = nested ? _parent!._operationRoot : this;
        if (!nested)
            while (root!._parent is not null) root = root._parent;
        if (root is null || (nested ? Volatile.Read(ref root._graphOperationThread) != thread
            : Interlocked.CompareExchange(ref root._graphOperationThread, thread, 0) != 0))
            throw new InvalidOperationException("Concurrent or reentrant operations cannot mutate the same behavior graph.");
        try
        {
            if (!nested && root._parent is not null)
                throw new InvalidOperationException("The graph was adopted while the operation was being admitted.");
            if (_executing)
                throw new InvalidOperationException("Behavior operations are not reentrant within an owning graph.");
            if (allowOwnedSignal && requireRunning)
                for (var parent = _parent; parent is not null; parent = parent._parent)
                    if (parent._status != BehaviorStatus.Running)
                        throw new InvalidOperationException("A signal cannot address an inactive or terminal composition.");
            if (requireRunning && _status != BehaviorStatus.Running)
                throw new InvalidOperationException("The behavior is not running.");
            _operationRoot = root;
            _ownsGraphOperation = !nested;
            _executing = true;
        }
        catch
        {
            if (!nested) Volatile.Write(ref root._graphOperationThread, 0);
            throw;
        }
    }

    private void EndOperation()
    {
        var root = _operationRoot!;
        bool release = _ownsGraphOperation;
        _executing = false;
        _operationRoot = null;
        _ownsGraphOperation = false;
        if (release) Volatile.Write(ref root._graphOperationThread, 0);
    }

    private sealed class ReferenceComparer : IEqualityComparer<Behavior<TContext>>
    {
        internal static readonly ReferenceComparer Instance = new();
        public bool Equals(Behavior<TContext>? left, Behavior<TContext>? right) => ReferenceEquals(left, right);
        public int GetHashCode(Behavior<TContext> value) => RuntimeHelpers.GetHashCode(value);
    }
}

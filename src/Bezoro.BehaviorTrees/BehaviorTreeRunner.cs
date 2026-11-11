namespace Bezoro.BehaviorTrees;

/// <summary>Executes one agent's tree with storage proportional to its maximum active path depth.</summary>
/// <remarks>
/// Definitions are shareable and independent runners can execute concurrently. Each update exclusively borrows its context
/// and handler; concurrent or reentrant lifecycle operations on one runner are rejected before mutation.
/// State inspection belongs to that execution owner. Actions are synchronous callbacks and own any external jobs.
/// No per-node tasks, blocking locks or progress arrays are created.
/// </remarks>
public sealed class BehaviorTreeRunner<TContext>
{
    private readonly long _owner = BehaviorOwnerId.Next();
    private readonly Frame[] _frames;
    private readonly Frame[] _resume;
    private readonly bool[] _conditionScratch;
    private int _count;
    private int _resumeCount;
    private int _firstReactive = -1;
    private int _activeNode = -1;
    private BehaviorActivation _activation;
    private ulong _serial;
    private bool _suspended;
    private int _busy;

    /// <summary>Allocates this runner's bounded path and optional condition/reactive scratch storage.</summary>
    public BehaviorTreeRunner(BehaviorTreeDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _frames = new Frame[definition.MaximumDepth];
        _resume = definition.MaximumReactiveDepth == 0 ? Array.Empty<Frame>() : new Frame[definition.MaximumReactiveDepth];
        _conditionScratch = definition.RequiredConditionStackSize == 0 ? Array.Empty<bool>() : new bool[definition.RequiredConditionStackSize];
    }
    /// <summary>Gets the shared immutable definition.</summary>
    public BehaviorTreeDefinition Definition { get; }
    /// <summary>Gets the current lifecycle status. Terminal results remain sticky until Stop or Restart.</summary>
    public BehaviorStatus Status { get; private set; }
    /// <summary>Gets the allocated frame capacity, which depends on depth rather than total node count.</summary>
    public int FrameCapacity => _frames.Length;
    /// <summary>Gets the optional snapshot capacity, bounded only by paths containing reactivity.</summary>
    public int ReactiveFrameCapacity => _resume.Length;
    /// <summary>Gets the active action occurrence, or -1 when no action is active.</summary>
    public int ActiveNodeId => _activeNode;
    /// <summary>Gets whether the supplied token still identifies the runner's live action.</summary>
    public bool IsActive(BehaviorActivation activation) => _activeNode >= 0 && activation.Owner == _owner && activation == _activation;

    /// <summary>Starts an inactive runner. Action callbacks begin during Tick, after consuming their visit.</summary>
    public void Start()
    {
        EnterOperation();
        try { StartCore(); }
        finally { ExitOperation(); }
    }

    private void StartCore()
    {
        if (Status != BehaviorStatus.Inactive) throw new InvalidOperationException("Stop or restart an existing execution before starting.");
        _count = 1;
        _frames[0] = new Frame(0);
        _firstReactive = IsReactive(Definition.Nodes[0].Kind) ? 0 : -1;
        _resumeCount = 0;
        _suspended = false;
        Status = BehaviorStatus.Running;
    }

    /// <summary>Updates with a reference handler, which may be shared when its own mutable state permits it.</summary>
    public BehaviorTickResult Tick(ref TContext context, IBehaviorTreeHandler<TContext> handler, ref BehaviorBudget budget)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        return Tick(ref context, ref handler, ref budget);
    }

    /// <summary>Updates using a handler by reference, avoiding boxing for struct handlers.</summary>
    public BehaviorTickResult Tick<THandler>(ref TContext context, ref THandler handler, ref BehaviorBudget budget)
        where THandler : IBehaviorTreeHandler<TContext>
    {
        EnterOperation();
        try { return TickCore(ref context, ref handler, ref budget); }
        finally { ExitOperation(); }
    }

    private BehaviorTickResult TickCore<THandler>(ref TContext context, ref THandler handler, ref BehaviorBudget budget)
        where THandler : IBehaviorTreeHandler<TContext>
    {
        if (!HandlerTraits<THandler>.IsValueType && handler is null) throw new ArgumentNullException(nameof(handler));
        if (Status == BehaviorStatus.Inactive) throw new InvalidOperationException("Start the runner before ticking it.");
        if (Status == BehaviorStatus.Faulted) throw new InvalidOperationException("Stop the faulted runner before starting again.");
        if (Status is BehaviorStatus.Success or BehaviorStatus.Failure) return new(Status, 0, false);
        if (budget.Remaining == 0) return new(Status, 0, true);
        int initialConsumed = budget.Consumed;
        try
        {
            if (!_suspended && _activeNode >= 0 && _firstReactive >= 0)
            {
                _resumeCount = _count;
                Array.Copy(_frames, _resume, _count);
                int node = _frames[_firstReactive].Node;
                _count = _firstReactive + 1;
                _frames[_firstReactive] = new Frame(node);
            }
            while (_count != 0)
            {
                int frameIndex = _count - 1;
                var frame = _frames[frameIndex];
                var node = Definition.Nodes[frame.Node];
                if (!frame.Initialized)
                {
                    if (!budget.TryConsume())
                    {
                        _suspended = true;
                        return new(Status, budget.Consumed - initialConsumed, true);
                    }
                    switch (node.Kind)
                    {
                        case BehaviorNodeKind.Action:
                            if (_activeNode != frame.Node)
                            {
                                AbortActive(ref context, ref handler);
                                if (_serial == ulong.MaxValue) throw new InvalidOperationException("Action activation serials are exhausted.");
                                _serial++;
                                _activeNode = frame.Node;
                                _activation = new BehaviorActivation(_owner, _serial);
                                handler.Enter(frame.Node, node.Parameter, _activation, ref context);
                            }
                            BehaviorStatus result = handler.Tick(frame.Node, node.Parameter, _activation, ref context, ref budget);
                            if (result == BehaviorStatus.Running)
                            {
                                _suspended = false;
                                _resumeCount = 0;
                                return new(Status, budget.Consumed - initialConsumed, false);
                            }
                            if (result is not (BehaviorStatus.Success or BehaviorStatus.Failure))
                                throw new InvalidOperationException("Actions may return Running, Success or Failure only.");
                            var completed = _activation;
                            _activeNode = -1;
                            _activation = default;
                            _resumeCount = 0;
                            handler.Exit(frame.Node, node.Parameter, completed, result, ref context);
                            Complete(result, ref context, ref handler);
                            continue;
                        case BehaviorNodeKind.Condition:
                            bool condition = node.Parameter >= 0
                                ? handler.Evaluate(node.Parameter, in context)
                                : Definition.Programs[~node.Parameter].Evaluate(in context, ref handler, _conditionScratch);
                            Complete(condition ? BehaviorStatus.Success : BehaviorStatus.Failure, ref context, ref handler);
                            continue;
                        case BehaviorNodeKind.Succeed:
                            Complete(BehaviorStatus.Success, ref context, ref handler);
                            continue;
                        case BehaviorNodeKind.Fail:
                            Complete(BehaviorStatus.Failure, ref context, ref handler);
                            continue;
                        default:
                            frame.Initialized = true;
                            frame.NextChild = frame.Node + 1;
                            frame.Iterations = 0;
                            _frames[frameIndex] = frame;
                            if (node.ChildCount == 0)
                            {
                                Complete(IsSelector(node.Kind) ? BehaviorStatus.Failure : BehaviorStatus.Success, ref context, ref handler);
                                continue;
                            }
                            break;
                    }
                }
                Push(frame.NextChild);
            }
            _suspended = false;
            _resumeCount = 0;
            return new(Status, budget.Consumed - initialConsumed, false);
        }
        catch (Exception failure)
        {
            Status = BehaviorStatus.Faulted;
            _count = 0;
            _firstReactive = -1;
            _resumeCount = 0;
            _suspended = false;
            try { AbortActive(ref context, ref handler); }
            catch (Exception cleanupFailure) { throw new AggregateException("Tree execution and action cleanup failed.", failure, cleanupFailure); }
            throw;
        }
    }

    /// <summary>Interrupts the current action once, discards progress, and returns to Inactive.</summary>
    public void Stop(ref TContext context, IBehaviorTreeHandler<TContext> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        Stop(ref context, ref handler);
    }
    /// <summary>Stops with a handler by reference, avoiding boxing for struct handlers.</summary>
    public void Stop<THandler>(ref TContext context, ref THandler handler) where THandler : IBehaviorTreeHandler<TContext>
    {
        EnterOperation();
        try { StopCore(ref context, ref handler); }
        finally { ExitOperation(); }
    }

    private void StopCore<THandler>(ref TContext context, ref THandler handler) where THandler : IBehaviorTreeHandler<TContext>
    {
        if (!HandlerTraits<THandler>.IsValueType && handler is null) throw new ArgumentNullException(nameof(handler));
        try
        {
            AbortActive(ref context, ref handler);
            Status = BehaviorStatus.Inactive;
        }
        catch { Status = BehaviorStatus.Faulted; throw; }
        finally
        {
            _count = 0; _resumeCount = 0; _firstReactive = -1; _suspended = false;
        }
    }
    /// <summary>Stops the current execution and starts a fresh one.</summary>
    public void Restart(ref TContext context, IBehaviorTreeHandler<TContext> handler)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));
        Restart(ref context, ref handler);
    }
    /// <summary>Restarts with a handler by reference.</summary>
    public void Restart<THandler>(ref TContext context, ref THandler handler) where THandler : IBehaviorTreeHandler<TContext>
    {
        EnterOperation();
        try { StopCore(ref context, ref handler); StartCore(); }
        finally { ExitOperation(); }
    }

    private void Push(int nodeId)
    {
        int depth = _count;
        var node = Definition.Nodes[nodeId];
        bool resume = depth < _resumeCount && _resume[depth].Node == nodeId && !IsReactive(node.Kind);
        _frames[_count++] = resume ? _resume[depth] : new Frame(nodeId);
        if (_firstReactive < 0 && IsReactive(node.Kind)) _firstReactive = depth;
    }

    private void Complete<THandler>(BehaviorStatus result, ref TContext context, ref THandler handler)
        where THandler : IBehaviorTreeHandler<TContext>
    {
        while (true)
        {
            int completedNode = _frames[_count - 1].Node;
            // A reactive guard may end a branch before visiting its former running action.
            if (_activeNode >= completedNode && _activeNode <= Definition.Nodes[completedNode].EndIndex)
                AbortActive(ref context, ref handler);
            _count--;
            if (_firstReactive == _count) _firstReactive = -1;
            if (_count == 0) { Status = result; return; }
            ref var parent = ref _frames[_count - 1];
            var node = Definition.Nodes[parent.Node];
            switch (node.Kind)
            {
                case BehaviorNodeKind.Sequence:
                case BehaviorNodeKind.ReactiveSequence:
                    if (result == BehaviorStatus.Failure) continue;
                    parent.NextChild = Definition.Nodes[completedNode].EndIndex + 1;
                    if (parent.NextChild > node.EndIndex) { result = BehaviorStatus.Success; continue; }
                    return;
                case BehaviorNodeKind.Selector:
                case BehaviorNodeKind.ReactiveSelector:
                    if (result == BehaviorStatus.Success) continue;
                    parent.NextChild = Definition.Nodes[completedNode].EndIndex + 1;
                    if (parent.NextChild > node.EndIndex) { result = BehaviorStatus.Failure; continue; }
                    return;
                case BehaviorNodeKind.Invert:
                    result = result == BehaviorStatus.Success ? BehaviorStatus.Failure : BehaviorStatus.Success;
                    continue;
                case BehaviorNodeKind.Repeat:
                    if (result == BehaviorStatus.Failure) continue;
                    parent.Iterations++;
                    if (parent.Iterations == node.Parameter) { result = BehaviorStatus.Success; continue; }
                    parent.NextChild = parent.Node + 1;
                    return;
                case BehaviorNodeKind.Subtree:
                    continue;
                default: throw new InvalidOperationException("Invalid compiled parent operation.");
            }
        }
    }

    private void AbortActive<THandler>(ref TContext context, ref THandler handler) where THandler : IBehaviorTreeHandler<TContext>
    {
        if (_activeNode < 0) return;
        int nodeId = _activeNode;
        var token = _activation;
        _activeNode = -1;
        _activation = default;
        _resumeCount = 0;
        handler.Abort(nodeId, Definition.Nodes[nodeId].Parameter, token, ref context);
    }
    private void EnterOperation()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("Concurrent or reentrant tree operations are not supported.");
    }
    private void ExitOperation() => Volatile.Write(ref _busy, 0);
    private static bool IsSelector(BehaviorNodeKind kind) => kind is BehaviorNodeKind.Selector or BehaviorNodeKind.ReactiveSelector;
    private static bool IsReactive(BehaviorNodeKind kind) => kind is BehaviorNodeKind.ReactiveSequence or BehaviorNodeKind.ReactiveSelector;
    private static class HandlerTraits<THandler>
    {
        internal static readonly bool IsValueType = typeof(THandler).IsValueType;
    }
    private struct Frame
    {
        internal int Node;
        internal int NextChild;
        internal int Iterations;
        internal bool Initialized;
        internal Frame(int node) { Node = node; NextChild = 0; Iterations = 0; Initialized = false; }
    }
}

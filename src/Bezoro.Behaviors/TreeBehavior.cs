using Bezoro.BehaviorTrees;

namespace Bezoro.Behaviors;

/// <summary>Adapts a behavior tree and its optional child-runtime bindings to the composition lifecycle.</summary>
/// <remarks>
/// Bound actions delegate their complete lifecycle to the child; unbound actions use the host handler.
/// A child suspended by an exhausted budget receives the next quantum before the parent's next reevaluation.
/// </remarks>
public sealed class TreeBehavior<TContext> : Behavior<TContext>
{
    private readonly BehaviorTreeRunner<TContext> _runner;
    private readonly IBehaviorTreeHandler<TContext>? _handler;
    private readonly TreeBehaviorBinding<TContext>[] _bindings;
    private Behavior<TContext>? _continuation;
    private BehaviorActivation _continuationActivation;
    private Behavior<TContext>? _tickedChild;
    private BehaviorActivation _tickedActivation;

    /// <summary>Creates an adapter, validating all bindings and ownership before adopting any child.</summary>
    public TreeBehavior(BehaviorTreeDefinition definition, IBehaviorTreeHandler<TContext>? handler = null,
        ReadOnlySpan<TreeBehaviorBinding<TContext>> bindings = default, BehaviorCompositionLimits limits = default)
        : this(CreateSetup(definition, handler, bindings, limits), limits) { }

    private TreeBehavior(Setup setup, BehaviorCompositionLimits limits) : base(setup.Children, limits, true)
    { _runner = setup.Runner; _handler = setup.Handler; _bindings = setup.Bindings; }

    /// <summary>Gets the shared immutable tree definition.</summary>
    public BehaviorTreeDefinition Definition => _runner.Definition;
    /// <summary>Gets the bindings ordered by compiled node occurrence.</summary>
    public ReadOnlySpan<TreeBehaviorBinding<TContext>> Bindings => _bindings;
    /// <summary>Tests whether an action token still belongs to this tree's active execution.</summary>
    public bool IsActive(BehaviorActivation activation) => CanAcceptSignals && _runner.IsActive(activation);

    /// <inheritdoc />
    protected override void OnStart(ref TContext context)
    {
        _continuation = null; _tickedChild = null;
        if (_runner.Status == BehaviorStatus.Faulted) StopRunner(ref context);
        _runner.Start();
    }

    /// <inheritdoc />
    protected override BehaviorStatus OnTick(ref TContext context, ref BehaviorBudget budget)
    {
        _tickedChild = null;
        try
        {
            if (_continuation is not null)
            {
                if (!_runner.IsActive(_continuationActivation)) _continuation = null;
                else
                {
                    if (budget.Remaining == 0)
                    { IsBudgetSuspended = true; return BehaviorStatus.Running; }
                    var child = _continuation;
                    if (child.Status == BehaviorStatus.Running) TickChild(child, ref context, ref budget);
                    _tickedChild = child;
                    _tickedActivation = _continuationActivation;
                    if (child.IsBudgetSuspended)
                    { IsBudgetSuspended = true; return BehaviorStatus.Running; }
                    _continuation = null;
                    if (budget.Remaining == 0)
                    {
                        // A terminal child still needs its parent's completion visit.
                        if (child.Status != BehaviorStatus.Running)
                        { _continuation = child; IsBudgetSuspended = true; }
                        return BehaviorStatus.Running;
                    }
                }
            }
            var routing = new RoutingHandler(this);
            var result = _runner.Tick(ref context, ref routing, ref budget);
            IsBudgetSuspended = result.BudgetExhausted || (_continuation is not null && _continuation.IsBudgetSuspended);
            return result.Status;
        }
        finally { _tickedChild = null; }
    }

    /// <inheritdoc />
    protected override void OnStop(ref TContext context)
    {
        _continuation = null; _tickedChild = null;
        try { StopRunner(ref context); }
        finally
        {
            // The runner invalidates its action before invoking Abort. A failed abort therefore
            // leaves no action to call again; this second stop clears only its technical fault.
            if (_runner.Status == BehaviorStatus.Faulted) StopRunner(ref context);
        }
    }

    /// <inheritdoc />
    protected override bool IsChildScopeLive(int childIndex) =>
        _runner.Status == BehaviorStatus.Running && _runner.ActiveNodeId == _bindings[childIndex].NodeId;

    private void StopRunner(ref TContext context)
    {
        var routing = new RoutingHandler(this);
        _runner.Stop(ref context, ref routing);
    }

    private readonly struct RoutingHandler : IBehaviorTreeHandler<TContext>
    {
        private readonly TreeBehavior<TContext> _owner;
        internal RoutingHandler(TreeBehavior<TContext> owner) => _owner = owner;

        public bool Evaluate(int predicateId, in TContext context) =>
            (_owner._handler ?? throw MissingHandler()).Evaluate(predicateId, in context);

        public void Enter(int nodeId, int actionId, BehaviorActivation activation, ref TContext context)
        {
            var child = _owner.FindChild(nodeId);
            if (child is null) (_owner._handler ?? throw MissingHandler()).Enter(nodeId, actionId, activation, ref context);
            else _owner.StartChild(child, ref context);
        }

        public BehaviorStatus Tick(int nodeId, int actionId, BehaviorActivation activation,
            ref TContext context, ref BehaviorBudget budget)
        {
            var child = _owner.FindChild(nodeId);
            if (child is null) return (_owner._handler ?? throw MissingHandler()).Tick(nodeId, actionId, activation, ref context, ref budget);
            BehaviorStatus result = child.Status;
            if (result == BehaviorStatus.Running &&
                (!ReferenceEquals(_owner._tickedChild, child) || _owner._tickedActivation != activation))
                result = _owner.TickChild(child, ref context, ref budget);
            if (result == BehaviorStatus.Running && child.IsBudgetSuspended)
            { _owner._continuation = child; _owner._continuationActivation = activation; }
            return result;
        }

        public void Exit(int nodeId, int actionId, BehaviorActivation activation,
            BehaviorStatus result, ref TContext context)
        {
            var child = _owner.FindChild(nodeId);
            if (child is null) (_owner._handler ?? throw MissingHandler()).Exit(nodeId, actionId, activation, result, ref context);
            else { _owner.ClearContinuation(child); _owner.StopChild(child, ref context); }
        }

        public void Abort(int nodeId, int actionId, BehaviorActivation activation, ref TContext context)
        {
            var child = _owner.FindChild(nodeId);
            if (child is null) (_owner._handler ?? throw MissingHandler()).Abort(nodeId, actionId, activation, ref context);
            else { _owner.ClearContinuation(child); _owner.StopChild(child, ref context); }
        }
    }

    private void ClearContinuation(Behavior<TContext> child)
    { if (ReferenceEquals(_continuation, child)) _continuation = null; }

    private Behavior<TContext>? FindChild(int nodeId)
    {
        int low = 0, high = _bindings.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int id = _bindings[middle].NodeId;
            if (id < nodeId) low = middle + 1;
            else if (id > nodeId) high = middle - 1;
            else return _bindings[middle].Child;
        }
        return null;
    }

    private static InvalidOperationException MissingHandler() => new("An unbound action or predicate requires a host handler.");

    private static Setup CreateSetup(BehaviorTreeDefinition definition, IBehaviorTreeHandler<TContext>? handler,
        ReadOnlySpan<TreeBehaviorBinding<TContext>> bindings, BehaviorCompositionLimits limits)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        BindingValidation<TContext>.ValidateCount(bindings.Length, limits);
        var copied = bindings.ToArray();
        Array.Sort(copied, BindingOrder.Instance);
        var children = copied.Length == 0 ? Array.Empty<Behavior<TContext>>() : new Behavior<TContext>[copied.Length];
        for (int i = 0; i < copied.Length; i++)
        {
            var binding = copied[i];
            if (binding.Child is null || binding.NodeId < 0 || binding.NodeId >= definition.NodeCount ||
                definition.GetNode(binding.NodeId).Kind != BehaviorNodeKind.Action ||
                (i > 0 && copied[i - 1].NodeId == binding.NodeId))
                throw new ArgumentException("Bindings must identify unique action occurrences and non-null children.", nameof(bindings));
            children[i] = binding.Child;
        }
        return new Setup(new BehaviorTreeRunner<TContext>(definition), handler, copied, children);
    }

    private readonly struct Setup
    {
        internal Setup(BehaviorTreeRunner<TContext> runner, IBehaviorTreeHandler<TContext>? handler,
            TreeBehaviorBinding<TContext>[] bindings, Behavior<TContext>[] children)
        { Runner = runner; Handler = handler; Bindings = bindings; Children = children; }
        internal BehaviorTreeRunner<TContext> Runner { get; }
        internal IBehaviorTreeHandler<TContext>? Handler { get; }
        internal TreeBehaviorBinding<TContext>[] Bindings { get; }
        internal Behavior<TContext>[] Children { get; }
    }
    private sealed class BindingOrder : IComparer<TreeBehaviorBinding<TContext>>
    {
        internal static readonly BindingOrder Instance = new();
        public int Compare(TreeBehaviorBinding<TContext> left, TreeBehaviorBinding<TContext> right) => left.NodeId.CompareTo(right.NodeId);
    }
}

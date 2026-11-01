using Bezoro.BehaviorTrees;

namespace Bezoro.Behaviors;

/// <summary>Binds owned child runtimes to named compiled actions without calculating preorder offsets.</summary>
/// <remarks>
/// Bind validates construction handles before changing the builder. Build adopts the children only after complete validation.
/// A successful Build consumes this builder because mutable children can have only one owner.
/// The runtime stores compact bindings and does not retain this builder, its compilation, handles, or expression map.
/// </remarks>
public sealed class TreeBehaviorBuilder<TContext>
{
    private readonly BehaviorTreeCompilation _compilation;
    private readonly IBehaviorTreeHandler<TContext>? _handler;
    private readonly List<TreeBehaviorBinding<TContext>> _bindings = new();
    private bool _built;

    /// <summary>Chooses the compiled tree and optional handler for its unbound actions and predicates.</summary>
    public TreeBehaviorBuilder(BehaviorTreeCompilation compilation, IBehaviorTreeHandler<TContext>? handler = null)
    {
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _handler = handler;
    }

    /// <summary>Binds one action occurrence to a child runtime, without adopting the child until Build.</summary>
    /// <exception cref="ArgumentException">The action is default, foreign, already bound, or the child is already bound in this builder.</exception>
    /// <exception cref="ArgumentNullException">The child is null.</exception>
    /// <exception cref="InvalidOperationException">Build already succeeded.</exception>
    public void Bind(BehaviorActionHandle action, Behavior<TContext> child)
    {
        RequireUnbuilt();
        int nodeId = _compilation.Resolve(action);
        if (child is null) throw new ArgumentNullException(nameof(child));
        foreach (TreeBehaviorBinding<TContext> binding in _bindings)
            if (binding.NodeId == nodeId || ReferenceEquals(binding.Child, child))
                throw new ArgumentException("Each action occurrence and child runtime can be bound only once.", nameof(action));
        _bindings.Add(new TreeBehaviorBinding<TContext>(nodeId, child));
    }

    /// <summary>Validates composition limits and ownership, then creates the runtime and consumes this builder.</summary>
    /// <remarks>A failed Build leaves the builder unconsumed and does not adopt any child.</remarks>
    /// <exception cref="InvalidOperationException">Build already succeeded.</exception>
    public TreeBehavior<TContext> Build(BehaviorCompositionLimits limits = default)
    {
        RequireUnbuilt();
        var result = new TreeBehavior<TContext>(_compilation.Definition, _handler, _bindings.ToArray(), limits);
        _built = true;
        return result;
    }

    private void RequireUnbuilt()
    {
        if (_built) throw new InvalidOperationException("This builder already transferred its child runtimes to a behavior.");
    }
}

namespace Bezoro.BehaviorTrees;

/// <summary>Compiles a tree while retaining construction-time action references for occurrence-safe binding.</summary>
/// <remarks>
/// Retain an Action expression in a named variable and call GetAction after compilation.
/// This resolver owns the expression-to-occurrence map; its immutable Definition and runners retain no such map.
/// Reusing the same expression twice is ambiguous. Use distinct Action expressions when binding separate occurrences.
/// </remarks>
public sealed class BehaviorTreeCompilation
{
    private readonly Dictionary<BehaviorNode, int> _actions = new();

    /// <summary>Compiles with the same expanded-node and depth limits as a standalone definition.</summary>
    public BehaviorTreeCompilation(BehaviorNode root, int maximumNodes = 16_384, int maximumDepth = 256)
        => Definition = new BehaviorTreeDefinition(root, maximumNodes, maximumDepth, _actions);

    /// <summary>Gets the immutable definition, which can outlive this construction-time resolver.</summary>
    public BehaviorTreeDefinition Definition { get; }

    /// <summary>Resolves a named Action expression that occurs exactly once in this construction graph.</summary>
    /// <remarks>Actions copied from a compiled Subtree have no construction-expression identity in this compilation.</remarks>
    /// <exception cref="ArgumentNullException">The expression is null.</exception>
    /// <exception cref="ArgumentException">The expression is not an action in this tree, or appears more than once.</exception>
    public BehaviorActionHandle GetAction(BehaviorNode action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        if (!_actions.TryGetValue(action, out int nodeId))
            throw new ArgumentException("The action expression does not occur in this compilation.", nameof(action));
        if (nodeId < 0)
            throw new ArgumentException("The action expression has multiple occurrences. Declare distinct Action expressions for separate bindings.", nameof(action));
        return new BehaviorActionHandle(this, nodeId);
    }

    /// <summary>Resolves one of this compilation's handles to its compact runtime node ID.</summary>
    /// <exception cref="ArgumentException">The handle is default or belongs to another compilation.</exception>
    public int Resolve(BehaviorActionHandle action)
    {
        if (!ReferenceEquals(action.Owner, this))
            throw new ArgumentException("The action handle must belong to this compilation.", nameof(action));
        return action.NodeId;
    }
}

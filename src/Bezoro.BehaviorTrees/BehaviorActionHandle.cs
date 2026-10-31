namespace Bezoro.BehaviorTrees;

/// <summary>Identifies one action occurrence resolved by a construction-time compilation.</summary>
/// <remarks>The default handle is invalid. Resolve it through its originating compilation before passing compact IDs to a runtime.</remarks>
public readonly struct BehaviorActionHandle
{
    internal BehaviorActionHandle(BehaviorTreeCompilation owner, int nodeId) { Owner = owner; NodeId = nodeId; }
    internal BehaviorTreeCompilation? Owner { get; }
    internal int NodeId { get; }
}

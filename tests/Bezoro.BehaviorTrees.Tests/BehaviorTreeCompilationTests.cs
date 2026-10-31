using Xunit;

namespace Bezoro.BehaviorTrees.Tests;

public sealed class BehaviorTreeCompilationTests
{
    [Fact]
    public void ActionResolutionFollowsItsOccurrenceWhenThePrefixChanges()
    {
        BehaviorNode reload = BehaviorNode.Action(7);
        var prefix = new BehaviorTreeDefinition(BehaviorNode.Repeat(BehaviorNode.Sequence(BehaviorNode.Succeed), 2));
        var compilation = new BehaviorTreeCompilation(BehaviorNode.Sequence(BehaviorNode.Subtree(prefix), reload));
        BehaviorActionHandle handle = compilation.GetAction(reload);
        int nodeId = compilation.Resolve(handle);
        Assert.Equal(compilation.Definition.NodeCount - 1, nodeId);
        Assert.Equal(7, compilation.Definition.GetNode(nodeId).ActionId);

        var changed = new BehaviorTreeCompilation(BehaviorNode.Sequence(BehaviorNode.Succeed, reload));
        Assert.NotEqual(nodeId, changed.Resolve(changed.GetAction(reload)));
        Assert.Throws<ArgumentException>(() => changed.Resolve(handle));
        Assert.Throws<ArgumentException>(() => compilation.Resolve(default));
    }

    [Fact]
    public void EqualActionIdsWithDifferentExpressionsRemainDistinct()
    {
        BehaviorNode first = BehaviorNode.Action(7);
        BehaviorNode second = BehaviorNode.Action(7);
        var compilation = new BehaviorTreeCompilation(BehaviorNode.Sequence(first, second));
        Assert.NotEqual(compilation.Resolve(compilation.GetAction(first)), compilation.Resolve(compilation.GetAction(second)));
    }

    [Fact]
    public void AmbiguousMissingAndNonActionExpressionsAreRejected()
    {
        BehaviorNode action = BehaviorNode.Action(7);
        BehaviorNode root = BehaviorNode.Sequence(action, action);
        var compilation = new BehaviorTreeCompilation(root);
        Assert.Throws<ArgumentException>(() => compilation.GetAction(action));
        Assert.Throws<ArgumentException>(() => compilation.GetAction(BehaviorNode.Action(7)));
        Assert.Throws<ArgumentException>(() => compilation.GetAction(root));
        Assert.Throws<ArgumentNullException>(() => compilation.GetAction(null!));
    }

    [Fact]
    public void CompiledSubtreeCopiesDoNotSilentlyReuseItsOriginalExpressionIdentity()
    {
        BehaviorNode action = BehaviorNode.Action(7);
        var subtree = new BehaviorTreeDefinition(action);
        var compilation = new BehaviorTreeCompilation(BehaviorNode.Subtree(subtree));
        Assert.Throws<ArgumentException>(() => compilation.GetAction(action));
        Assert.Equal(7, compilation.Definition.GetNode(1).ActionId);
    }

    [Fact]
    public void CompilationPreservesDefinitionBounds()
    {
        Assert.Throws<ArgumentNullException>(() => new BehaviorTreeCompilation(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BehaviorTreeCompilation(BehaviorNode.Succeed, maximumNodes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BehaviorTreeCompilation(BehaviorNode.Succeed, maximumDepth: 0));
        Assert.Throws<ArgumentException>(() => new BehaviorTreeCompilation(BehaviorNode.Invert(BehaviorNode.Succeed), maximumDepth: 1));
    }
}

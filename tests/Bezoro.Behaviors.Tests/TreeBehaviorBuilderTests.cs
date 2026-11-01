using System.Runtime.CompilerServices;
using Bezoro.BehaviorTrees;
using Xunit;

namespace Bezoro.Behaviors.Tests;

public sealed class TreeBehaviorBuilderTests
{
    [Fact]
    public void NamedActionBindingExecutesItsChildAcrossBudgetOneYields()
    {
        BehaviorNode action = BehaviorNode.Action(99);
        var compilation = new BehaviorTreeCompilation(BehaviorNode.Sequence(
            BehaviorNode.Repeat(BehaviorNode.Succeed, 3), action));
        var child = new Leaf();
        var builder = new TreeBehaviorBuilder<int>(compilation);
        builder.Bind(compilation.GetAction(action), child);
        TreeBehavior<int> behavior = builder.Build();
        int context = 0;
        behavior.Start(ref context);
        for (int tick = 0; tick < 20 && behavior.Status == BehaviorStatus.Running; tick++)
        {
            var budget = new BehaviorBudget(1);
            behavior.Tick(ref context, ref budget);
            Assert.InRange(budget.Consumed, 0, 1);
        }
        Assert.Equal(BehaviorStatus.Success, behavior.Status);
        Assert.Equal(1, context);
        Assert.Equal(1, child.Stops);
        Assert.Equal(compilation.Resolve(compilation.GetAction(action)), behavior.Bindings[0].NodeId);
        Assert.Equal(2 * IntPtr.Size, Unsafe.SizeOf<TreeBehaviorBinding<int>>());
        Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Throws<InvalidOperationException>(() => builder.Bind(compilation.GetAction(action), new Leaf()));
    }

    [Fact]
    public void RejectedBindingsLeaveExistingChildrenAvailableForBuild()
    {
        BehaviorNode first = BehaviorNode.Action(0);
        BehaviorNode second = BehaviorNode.Action(0);
        var compilation = new BehaviorTreeCompilation(BehaviorNode.Sequence(first, second));
        var foreign = new BehaviorTreeCompilation(first);
        var builder = new TreeBehaviorBuilder<int>(compilation);
        var child = new Leaf();
        var spare = new Leaf();
        builder.Bind(compilation.GetAction(first), child);
        Assert.Throws<ArgumentException>(() => builder.Bind(default, spare));
        Assert.Throws<ArgumentException>(() => builder.Bind(foreign.GetAction(first), spare));
        Assert.Throws<ArgumentNullException>(() => builder.Bind(compilation.GetAction(second), null!));
        Assert.Throws<ArgumentException>(() => builder.Bind(compilation.GetAction(first), spare));
        Assert.Throws<ArgumentException>(() => builder.Bind(compilation.GetAction(second), child));
        builder.Bind(compilation.GetAction(second), spare);
        TreeBehavior<int> behavior = builder.Build();
        Assert.Equal(2, behavior.Bindings.Length);
    }

    [Fact]
    public void FailedBuildDoesNotAdoptChildrenOrConsumeBuilder()
    {
        BehaviorNode action = BehaviorNode.Action(0);
        var compilation = new BehaviorTreeCompilation(action);
        var child = new Leaf();
        var builder = new TreeBehaviorBuilder<int>(compilation);
        builder.Bind(compilation.GetAction(action), child);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Build(new BehaviorCompositionLimits(1)));

        int context = 0;
        child.Start(ref context);
        child.Stop(ref context);
        TreeBehavior<int> behavior = builder.Build(new BehaviorCompositionLimits(2));
        Assert.Equal(2, behavior.CompositionDepth);
        Assert.Throws<InvalidOperationException>(() => child.Start(ref context));
    }

    [Fact]
    public void ConstructorRequiresACompilation()
        => Assert.Throws<ArgumentNullException>(() => new TreeBehaviorBuilder<int>(null!));

    [Fact]
    public void RuntimeDoesNotRetainConstructionObjects()
    {
        (TreeBehavior<int> behavior, WeakReference[] construction) = CreateRuntime();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        foreach (WeakReference reference in construction) Assert.False(reference.IsAlive);

        int context = 0;
        behavior.Start(ref context);
        var budget = new BehaviorBudget(2);
        Assert.Equal(BehaviorStatus.Success, behavior.Tick(ref context, ref budget));
        Assert.Equal(1, context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (TreeBehavior<int> Behavior, WeakReference[] Construction) CreateRuntime()
    {
        BehaviorNode action = BehaviorNode.Action(0);
        var compilation = new BehaviorTreeCompilation(action);
        var builder = new TreeBehaviorBuilder<int>(compilation);
        builder.Bind(compilation.GetAction(action), new Leaf());
        return (builder.Build(), new[] { new WeakReference(action), new WeakReference(compilation), new WeakReference(builder) });
    }

    private sealed class Leaf : Behavior<int>
    {
        internal int Stops;
        protected override void OnStart(ref int context) { }
        protected override BehaviorStatus OnTick(ref int context, ref BehaviorBudget budget)
        {
            if (!budget.TryConsume()) { IsBudgetSuspended = true; return BehaviorStatus.Running; }
            context++;
            return BehaviorStatus.Success;
        }
        protected override void OnStop(ref int context) => Stops++;
    }
}

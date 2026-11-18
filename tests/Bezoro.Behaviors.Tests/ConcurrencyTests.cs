using Bezoro.BehaviorTrees;
using Bezoro.Conditions;
using Bezoro.Statecharts;
using Bezoro.StateMachines;
using Xunit;

namespace Bezoro.Behaviors.Tests;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task ADescendantSignalExcludesRootAndSiblingOperationsAcrossThreads()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var source = new StatechartBehavior<int>(Definition(), new BlockingHandler(entered, release));
        var sibling = new StateMachineBehavior<int>(new StateMachineDefinition(2, 1,
            new[] { new StateTransition(new StateId(0), new SignalId(0), new StateId(1)) }));
        var root = new Group(source, sibling);
        int context = 0;
        root.Start(ref context);
        StateActivation token = source.GetActivation(new StateId(0));
        ulong version = sibling.ActivationVersion;
        var signal = Task.Factory.StartNew(() =>
        {
            int workerContext = 0;
            return source.Dispatch(new SignalId(0), ref workerContext);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            int otherContext = 0;
            var budget = new BehaviorBudget(16);
            Assert.Throws<InvalidOperationException>(() => root.Stop(ref otherContext));
            Assert.Throws<InvalidOperationException>(() => root.Tick(ref otherContext, ref budget));
            Assert.Throws<InvalidOperationException>(() => sibling.Dispatch(new SignalId(0), ref otherContext));
            Assert.Throws<InvalidOperationException>(() => sibling.TryDispatch(version, new SignalId(0), ref otherContext));
            Assert.Throws<InvalidOperationException>(() => source.TryDispatch(token, new SignalId(0), ref otherContext));
            Assert.Equal(0, otherContext);
        }
        finally { release.Set(); }
        Assert.Equal(1, (await signal.WaitAsync(TimeSpan.FromSeconds(10))).TransitionCount);
        Assert.False(source.IsActive(token));
        Assert.True(sibling.TryDispatch(version, new SignalId(0), ref context));
        Assert.False(sibling.TryDispatch(version, new SignalId(0), ref context));
        root.Stop(ref context);
        Assert.Equal(BehaviorStatus.Inactive, root.Status);
    }

    [Fact]
    public void ConcurrentConstructorsCannotShareOneChildAndRejectedClaimsLeaveOtherChildrenReusable()
    {
        var child = new Leaf();
        var accepted = new System.Collections.Concurrent.ConcurrentBag<Group>();
        Parallel.For(0, 32, _ =>
        {
            try { accepted.Add(new Group(child)); }
            catch (ArgumentException) { }
        });
        var parent = Assert.Single(accepted);
        var reusable = new Leaf();
        Assert.Throws<ArgumentException>(() => new Group(reusable, child));
        var other = new Group(reusable);
        int firstContext = 0, secondContext = 0;
        parent.Start(ref firstContext);
        other.Start(ref secondContext);
        Assert.Equal(1, firstContext);
        Assert.Equal(1, secondContext);
        parent.Stop(ref firstContext);
        other.Stop(ref secondContext);
        Assert.Equal(0, firstContext);
        Assert.Equal(0, secondContext);
    }

    [Fact]
    public void AdoptionAndDirectStartCannotBothTakeOwnership()
    {
        using var gate = new Barrier(2);
        var children = Enumerable.Range(0, 512).Select(_ => new Leaf()).ToArray();
        var parents = new Group?[children.Length];
        var started = new bool[children.Length];
        Parallel.Invoke(() =>
        {
            for (int index = 0; index < children.Length; index++)
            {
                gate.SignalAndWait();
                int context = 0;
                try { children[index].Start(ref context); started[index] = true; }
                catch (InvalidOperationException) { }
            }
        }, () =>
        {
            for (int index = 0; index < children.Length; index++)
            {
                gate.SignalAndWait();
                try { parents[index] = new Group(children[index]); }
                catch (ArgumentException) { }
            }
        });
        for (int index = 0; index < children.Length; index++)
        {
            int context = 0;
            var parent = parents[index];
            if (parent is null)
            {
                // If both contenders saw the other reservation, retry from an inactive root.
                if (!started[index]) children[index].Start(ref context);
                children[index].Stop(ref context);
            }
            else
            {
                Assert.False(started[index]);
                Assert.Throws<InvalidOperationException>(() => children[index].Start(ref context));
                parent.Start(ref context);
                parent.Stop(ref context);
            }
            Assert.Equal(BehaviorStatus.Inactive, children[index].Status);
        }
    }

    [Fact]
    public void CompositionAdaptersCannotBeUsedAsCallbackReceivers()
    {
        Assert.False(typeof(IStatechartHandler<int>).IsAssignableFrom(typeof(StatechartBehavior<int>)));
        Assert.False(typeof(IBehaviorTreeHandler<int>).IsAssignableFrom(typeof(TreeBehavior<int>)));
    }

    [Fact]
    public void IndependentGraphsExecuteInParallelWithTheirOwnContexts()
    {
        var definition = Definition();
        Parallel.For(0, 32, _ =>
        {
            var chart = new StatechartBehavior<int>(definition, new PassingHandler());
            var root = new Group(chart, new Leaf());
            int context = 0;
            root.Start(ref context);
            Assert.Equal(1, context);
            chart.Dispatch(new SignalId(0), ref context);
            Assert.True(chart.IsActive(new StateId(1)));
            root.Stop(ref context);
            Assert.Equal(0, context);
        });
    }

    private static StatechartDefinition Definition() => new(
        new[] { new ChartState(new RegionId(0)), new ChartState(new RegionId(0)) },
        new[] { new ChartRegion(new StateId(0)) },
        new[] { new ChartTransition(new StateId(0), new SignalId(0), new StateId(1), ConditionExpression.Predicate(0).Compile()) }, 1);

    private sealed class Group(params Behavior<int>[] children) : Behavior<int>(children, default)
    {
        protected override void OnStart(ref int context)
        { foreach (var child in Children) StartChild(child, ref context); }
        protected override BehaviorStatus OnTick(ref int context, ref BehaviorBudget budget)
        {
            foreach (var child in Children) TickChild(child, ref context, ref budget);
            return BehaviorStatus.Running;
        }
        protected override void OnStop(ref int context) { }
    }
    private sealed class Leaf : Behavior<int>
    {
        protected override void OnStart(ref int context) => context++;
        protected override BehaviorStatus OnTick(ref int context, ref BehaviorBudget budget) => BehaviorStatus.Running;
        protected override void OnStop(ref int context) => context--;
    }
    private sealed class PassingHandler : StatechartHandler<int>
    {
        public override bool EvaluateGuard(int predicateId, StatechartView configuration, in int context) => true;
    }
    private sealed class BlockingHandler(ManualResetEventSlim entered, ManualResetEventSlim release) : StatechartHandler<int>
    {
        public override bool EvaluateGuard(int predicateId, StatechartView configuration, in int context)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return true;
        }
    }
}

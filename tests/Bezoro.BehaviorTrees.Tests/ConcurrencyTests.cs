using Xunit;

namespace Bezoro.BehaviorTrees.Tests;

public sealed class ConcurrencyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AnActiveTickRejectsOverlappingLifecycleOperationsBeforeEffects(int operation)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var handler = new BlockingHandler(entered, release);
        var runner = new BehaviorTreeRunner<int>(new BehaviorTreeDefinition(BehaviorNode.Action(0)));
        runner.Start();
        var tick = Task.Factory.StartNew(() =>
        {
            int context = 0;
            var budget = new BehaviorBudget(1);
            return runner.Tick(ref context, handler, ref budget);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            int otherContext = 0;
            var otherBudget = new BehaviorBudget(1);
            Assert.Throws<InvalidOperationException>(() =>
            {
                switch (operation)
                {
                    case 0: runner.Start(); break;
                    case 1: runner.Stop(ref otherContext, handler); break;
                    case 2: runner.Restart(ref otherContext, handler); break;
                    default: runner.Tick(ref otherContext, handler, ref otherBudget); break;
                }
            });
            Assert.Equal(0, handler.Aborts);
        }
        finally { release.Set(); }
        Assert.Equal(BehaviorStatus.Running, (await tick.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        int finalContext = 0;
        runner.Stop(ref finalContext, handler);
        Assert.Equal(1, handler.Aborts);
    }

    [Fact]
    public void IndependentRunnersShareDefinitionsAcrossParallelWorkers()
    {
        var definition = new BehaviorTreeDefinition(BehaviorNode.Repeat(BehaviorNode.Action(0), 100));
        Parallel.For(0, 32, _ =>
        {
            var runner = new BehaviorTreeRunner<int>(definition);
            var handler = new CountingHandler();
            int context = 0;
            runner.Start();
            var budget = new BehaviorBudget(1024);
            Assert.Equal(BehaviorStatus.Success, runner.Tick(ref context, ref handler, ref budget).Status);
            Assert.Equal(100, context);
        });
    }

    [Fact]
    public void ArgumentAndLifecycleFailuresReleaseAdmissionBeforeTheNextCall()
    {
        var runner = new BehaviorTreeRunner<int>(new BehaviorTreeDefinition(BehaviorNode.Succeed));
        int context = 0;
        var handler = new CountingHandler();
        var budget = new BehaviorBudget(1);
        Assert.Throws<InvalidOperationException>(() => runner.Tick(ref context, ref handler, ref budget));
        runner.Start();
        Assert.Throws<InvalidOperationException>(() => runner.Start());
        Assert.Equal(BehaviorStatus.Success, runner.Tick(ref context, ref handler, ref budget).Status);
        runner.Restart(ref context, ref handler);
        Assert.Equal(BehaviorStatus.Running, runner.Status);
    }

    private readonly struct CountingHandler : IBehaviorTreeHandler<int>
    {
        public bool Evaluate(int predicateId, in int context) => true;
        public void Enter(int nodeId, int actionId, BehaviorActivation activation, ref int context) { }
        public BehaviorStatus Tick(int nodeId, int actionId, BehaviorActivation activation, ref int context, ref BehaviorBudget budget)
        { context++; return BehaviorStatus.Success; }
        public void Exit(int nodeId, int actionId, BehaviorActivation activation, BehaviorStatus result, ref int context) { }
        public void Abort(int nodeId, int actionId, BehaviorActivation activation, ref int context) { }
    }

    private sealed class BlockingHandler(ManualResetEventSlim entered, ManualResetEventSlim release) : IBehaviorTreeHandler<int>
    {
        internal int Aborts;
        public bool Evaluate(int predicateId, in int context) => true;
        public void Enter(int nodeId, int actionId, BehaviorActivation activation, ref int context) { }
        public BehaviorStatus Tick(int nodeId, int actionId, BehaviorActivation activation, ref int context, ref BehaviorBudget budget)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return BehaviorStatus.Running;
        }
        public void Exit(int nodeId, int actionId, BehaviorActivation activation, BehaviorStatus result, ref int context) { }
        public void Abort(int nodeId, int actionId, BehaviorActivation activation, ref int context) => Aborts++;
    }
}

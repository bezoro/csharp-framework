using Bezoro.Conditions;
using Bezoro.StateMachines;
using Xunit;

namespace Bezoro.Statecharts.Tests;

public sealed class ConcurrencyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DispatchExcludesConcurrentLifecycleCallsBeforeAnyExit(int operation)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var handler = new BlockingHandler(entered, release);
        var runner = new StatechartRunner<int>(Definition(), handler);
        int context = 0;
        runner.Start(ref context);
        var dispatch = Task.Factory.StartNew(() =>
        {
            int workerContext = 0;
            return runner.Dispatch(new SignalId(0), ref workerContext);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            int otherContext = 0;
            Assert.Throws<InvalidOperationException>(() =>
            {
                switch (operation)
                {
                    case 0: runner.Start(ref otherContext); break;
                    case 1: runner.Stop(ref otherContext); break;
                    case 2: runner.Reset(ref otherContext); break;
                    default: runner.Dispatch(new SignalId(0), ref otherContext); break;
                }
            });
            Assert.Equal(0, handler.Exits);
        }
        finally { release.Set(); }
        Assert.Equal(1, (await dispatch.WaitAsync(TimeSpan.FromSeconds(10))).TransitionCount);
        Assert.True(runner.IsRunning);
        Assert.True(runner.IsActive(new StateId(1)));
        runner.Stop(ref context);
        Assert.Equal(2, handler.Exits);
    }

    [Fact]
    public void InvalidSignalsReleaseAdmissionWithoutFaultingOrChangingConfiguration()
    {
        var runner = new StatechartRunner<int>(Definition());
        int context = 0;
        runner.Start(ref context);
        Assert.Throws<ArgumentOutOfRangeException>(() => runner.Dispatch(new SignalId(1), ref context));
        Assert.True(runner.IsRunning);
        Assert.False(runner.IsFaulted);
        Assert.True(runner.IsActive(new StateId(0)));
        runner.Reset(ref context);
        runner.Start(ref context);
        Assert.True(runner.IsRunning);
    }

    private static StatechartDefinition Definition() => new(
        new[] { new ChartState(new RegionId(0)), new ChartState(new RegionId(0)) },
        new[] { new ChartRegion(new StateId(0)) },
        new[] { new ChartTransition(new StateId(0), new SignalId(0), new StateId(1), ConditionExpression.Predicate(0).Compile()) }, 1);

    private sealed class BlockingHandler(ManualResetEventSlim entered, ManualResetEventSlim release) : StatechartHandler<int>
    {
        internal int Exits;
        public override bool EvaluateGuard(int predicateId, StatechartView configuration, in int context)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return true;
        }
        public override void OnExit(StateId state, StateActivation activation, ref int context) => Exits++;
    }
}

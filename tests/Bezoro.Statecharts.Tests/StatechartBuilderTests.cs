using Bezoro.StateMachines;
using Xunit;

namespace Bezoro.Statecharts.Tests;

public sealed class StatechartBuilderTests
{
    [Fact]
    public void HierarchyAndParallelRegionsFollowTheDeclarations()
    {
        var chart = new StatechartBuilder();
        StatechartRegion controller = chart.Region();
        StateHandle active = controller.State();
        controller.StartIn(active);
        StatechartRegion movement = chart.Region(active);
        StateHandle idle = movement.State();
        StateHandle walking = movement.State();
        movement.StartIn(idle);
        StatechartRegion reaction = chart.Region(active);
        StateHandle calm = reaction.State();
        StateHandle hit = reaction.State();
        reaction.StartIn(calm);
        SignalHandle advance = chart.Signal();
        chart.Transition(idle, advance, walking);
        chart.Transition(calm, advance, hit);

        StatechartDefinition definition = chart.Build();
        var runner = new StatechartRunner<int>(definition);
        int context = 0;
        runner.Start(ref context);
        StateActivation parent = runner.GetActivation(chart.Resolve(active));
        Assert.Equal(3, definition.MaximumActiveStates);
        Assert.True(runner.IsActive(chart.Resolve(idle)));
        Assert.True(runner.IsActive(chart.Resolve(calm)));
        StatechartDispatchResult result = runner.Dispatch(chart.Resolve(advance), ref context);
        Assert.Equal(2, result.TransitionCount);
        Assert.True(runner.IsActive(chart.Resolve(walking)));
        Assert.True(runner.IsActive(chart.Resolve(hit)));
        Assert.True(runner.IsActive(parent));
    }

    [Fact]
    public void ForeignDefaultAndWrongRegionHandlesDoNotMutateTheBuilder()
    {
        var chart = new StatechartBuilder();
        StatechartRegion root = chart.Region();
        StateHandle state = root.State();
        root.StartIn(state);
        SignalHandle signal = chart.Signal();
        StatechartRegion parallel = chart.Region();
        StateHandle otherRegion = parallel.State();
        parallel.StartIn(otherRegion);
        var foreign = new StatechartBuilder();
        StateHandle foreignState = foreign.Region().State();
        SignalHandle foreignSignal = foreign.Signal();

        Assert.Throws<ArgumentException>(() => root.StartIn(otherRegion));
        Assert.Throws<ArgumentException>(() => root.StartIn(foreignState));
        Assert.Throws<ArgumentException>(() => root.StartIn(default));
        Assert.Throws<ArgumentException>(() => chart.Region(foreignState));
        Assert.Throws<ArgumentException>(() => chart.Region(default));
        Assert.Throws<ArgumentException>(() => chart.Resolve(default(StateHandle)));
        Assert.Throws<ArgumentException>(() => chart.Resolve(default(SignalHandle)));
        Assert.Throws<ArgumentException>(() => chart.Transition(foreignState, signal, state));
        Assert.Throws<ArgumentException>(() => chart.Transition(state, foreignSignal, state));
        Assert.Throws<ArgumentException>(() => chart.Transition(state, signal, foreignState));
        Assert.Throws<ArgumentException>(() => chart.Transition(state, signal, state, kind: (ChartTransitionKind)100));
        Assert.Throws<InvalidOperationException>(() => default(StatechartRegion).State());
        Assert.Throws<InvalidOperationException>(() => default(StatechartRegion).StartIn(state));

        StatechartDefinition definition = chart.Build();
        Assert.Equal(2, definition.RegionCount);
        Assert.Equal(2, definition.StateCount);
        Assert.Equal(0, definition.Transitions.Length);
        int context = 0;
        var runner = new StatechartRunner<int>(definition);
        runner.Start(ref context);
        Assert.True(runner.IsActive(chart.Resolve(state)));
        Assert.True(runner.IsActive(chart.Resolve(otherRegion)));
    }

    [Fact]
    public void BuildSnapshotsAreIndependentAndMissingInitialStatesCanBeCompleted()
    {
        var chart = new StatechartBuilder();
        Assert.Throws<InvalidOperationException>(() => chart.Build());
        StatechartRegion root = chart.Region();
        StateHandle first = root.State();
        StateHandle second = root.State();
        Assert.Throws<InvalidOperationException>(() => chart.Build());
        root.StartIn(first);
        StatechartDefinition before = chart.Build();
        root.StartIn(second);
        StatechartDefinition after = chart.Build();
        int context = 0;
        var firstRunner = new StatechartRunner<int>(before);
        var secondRunner = new StatechartRunner<int>(after);
        firstRunner.Start(ref context);
        secondRunner.Start(ref context);
        Assert.True(firstRunner.IsActive(chart.Resolve(first)));
        Assert.False(firstRunner.IsActive(chart.Resolve(second)));
        Assert.True(secondRunner.IsActive(chart.Resolve(second)));
    }

    [Fact]
    public void InvalidLifecycleDomainsAreRejectedBeforeExposingADefinition()
    {
        var chart = new StatechartBuilder();
        StatechartRegion root = chart.Region();
        StateHandle first = root.State();
        StateHandle second = root.State();
        root.StartIn(first);
        chart.Transition(first, chart.Signal(), second, kind: ChartTransitionKind.Local);
        Assert.Throws<ArgumentException>(() => chart.Build());
    }
}

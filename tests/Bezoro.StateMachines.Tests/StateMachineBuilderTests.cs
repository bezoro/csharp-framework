using Xunit;

namespace Bezoro.StateMachines.Tests;

public sealed class StateMachineBuilderTests
{
    [Fact]
    public void ChestDeclarationsNeedNoPositionalIdsAndBuildIndependentSnapshots()
    {
        var chest = new StateMachineBuilder();
        StateHandle closed = chest.State();
        StateHandle open = chest.State();
        SignalHandle unlock = chest.Signal();
        SignalHandle close = chest.Signal();
        chest.StartIn(closed);
        chest.Transition(from: closed, on: unlock, to: open);
        StateMachineDefinition first = chest.Build();

        chest.Transition(from: open, on: close, to: closed);
        chest.StartIn(open);
        StateMachineDefinition second = chest.Build();

        Assert.Equal(chest.Resolve(closed), first.InitialState);
        Assert.Equal(chest.Resolve(open), second.InitialState);
        Assert.Equal(chest.Resolve(open), first.Step(first.InitialState, chest.Resolve(unlock)).CurrentState);
        Assert.False(first.Step(chest.Resolve(open), chest.Resolve(close)).HasTransition);
        Assert.Equal(chest.Resolve(closed), second.Step(second.InitialState, chest.Resolve(close)).CurrentState);
        Assert.Equal(2, second.StateCount);
        Assert.Equal(2, second.SignalCount);
    }

    [Fact]
    public void DefaultAndForeignHandlesAreRejectedBeforeChangingTheDeclarations()
    {
        var machine = new StateMachineBuilder();
        var foreign = new StateMachineBuilder();
        StateHandle state = machine.State();
        SignalHandle signal = machine.Signal();
        StateHandle foreignState = foreign.State();
        SignalHandle foreignSignal = foreign.Signal();
        machine.StartIn(state);

        Assert.Throws<ArgumentException>(() => machine.StartIn(default));
        Assert.Throws<ArgumentException>(() => machine.StartIn(foreignState));
        Assert.Throws<ArgumentException>(() => machine.Resolve(default(StateHandle)));
        Assert.Throws<ArgumentException>(() => machine.Resolve(default(SignalHandle)));
        Assert.Throws<ArgumentException>(() => machine.Transition(foreignState, signal, state));
        Assert.Throws<ArgumentException>(() => machine.Transition(state, foreignSignal, state));
        Assert.Throws<ArgumentException>(() => machine.Transition(state, signal, foreignState));
        Assert.Throws<ArgumentException>(() => machine.Transition(state, default, state));

        StateMachineDefinition definition = machine.Build();
        Assert.Equal(machine.Resolve(state), definition.InitialState);
        Assert.Equal(0, definition.TransitionCount);
        machine.Transition(state, signal, state);
        Assert.True(machine.Build().Step(definition.InitialState, machine.Resolve(signal)).HasTransition);
    }

    [Fact]
    public void InitialStateMustBeExplicitAndNoInputStorageIsRequired()
    {
        var machine = new StateMachineBuilder();
        Assert.Throws<InvalidOperationException>(() => machine.Build());
        StateHandle state = machine.State();
        Assert.Throws<InvalidOperationException>(() => machine.Build());
        machine.StartIn(state);
        StateMachineDefinition definition = machine.Build();
        Assert.Equal(1, definition.StateCount);
        Assert.Equal(0, definition.SignalCount);
        Assert.Equal(0, definition.TransitionCount);
    }

    [Fact]
    public void DuplicateRulesAreRejectedByCompilation()
    {
        var machine = new StateMachineBuilder();
        StateHandle first = machine.State();
        StateHandle second = machine.State();
        SignalHandle signal = machine.Signal();
        machine.StartIn(first);
        machine.Transition(first, signal, second);
        machine.Transition(first, signal, first);
        Assert.Throws<ArgumentException>(() => machine.Build());
    }
}

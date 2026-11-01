using BenchmarkDotNet.Attributes;
using Bezoro.BehaviorTrees;
using Bezoro.StateMachines;

namespace Bezoro.Behaviors.Benchmarks;

/// <summary>Measures warmed composed execution with fixed contexts and a shared work allowance.</summary>
[MemoryDiagnoser]
public class CompositionBenchmarks
{
    private TreeBehavior<Context> _nested = null!;
    private Context _context;

    /// <summary>Gets or sets the configured runtime depth.</summary>
    [Params(1, 8, 32)]
    public int Depth { get; set; }

    /// <summary>Builds graphs once before timing.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var action = BehaviorNode.Action(0);
        var compilation = new BehaviorTreeCompilation(action);
        var handler = new Handler();
        var limits = new BehaviorCompositionLimits(64, 256);
        _nested = new TreeBehavior<Context>(compilation.Definition, handler);
        for (int i = 1; i < Depth; i++)
        {
            var builder = new TreeBehaviorBuilder<Context>(compilation);
            builder.Bind(compilation.GetAction(action), _nested);
            _nested = builder.Build(limits);
        }
        _nested.Start(ref _context);
    }

    /// <summary>Advances one quantum; visits include every nested evaluator charged to the allowance.</summary>
    [Benchmark]
    public int NestedBudgetOne()
    {
        var budget = new BehaviorBudget(1);
        _nested.Tick(ref _context, ref budget);
        return budget.Consumed;
    }

    /// <summary>Advances a composed running path with enough allowance for this configured depth.</summary>
    [Benchmark]
    public int NestedFullPath()
    {
        var budget = new BehaviorBudget(128);
        _nested.Tick(ref _context, ref budget);
        return budget.Consumed;
    }

    /// <summary>Stops the graph after measurement.</summary>
    [GlobalCleanup]
    public void Cleanup()
    { _nested.Stop(ref _context); }

}

/// <summary>Measures explicit transitions in a fixed two-state composed machine.</summary>
[MemoryDiagnoser]
public class StateMachineCompositionBenchmarks
{
    private StateMachineBehavior<Context> _machine = null!;
    private SignalId _change;
    private Context _context;

    [GlobalSetup]
    public void Setup()
    {
        var definition = new BehaviorTreeDefinition(BehaviorNode.Action(0));
        var handler = new Handler();
        var states = new StateMachineBuilder();
        var first = states.State();
        var second = states.State();
        var change = states.Signal();
        states.StartIn(first);
        states.Transition(first, change, second);
        states.Transition(second, change, first);
        _machine = new StateMachineBehavior<Context>(states.Build(), new[]
        {
            new StateBehaviorBinding<Context>(states.Resolve(first), new TreeBehavior<Context>(definition, handler)),
            new StateBehaviorBinding<Context>(states.Resolve(second), new TreeBehavior<Context>(definition, handler))
        });
        _change = states.Resolve(change);
        _machine.Start(ref _context);
    }

    /// <summary>Transitions between two owned trees and updates the new state once.</summary>
    [Benchmark]
    public long TransitionAndTick()
    {
        _machine.Dispatch(_change, ref _context);
        var budget = new BehaviorBudget(4);
        _machine.Tick(ref _context, ref budget);
        return _context.Ticks;
    }

    [GlobalCleanup]
    public void Cleanup() => _machine.Stop(ref _context);
}

internal struct Context { internal long Ticks; }
internal sealed class Handler : IBehaviorTreeHandler<Context>
{
    public bool Evaluate(int predicateId, in Context context) => true;
    public void Enter(int nodeId, int actionId, BehaviorActivation activation, ref Context context) { }
    public BehaviorStatus Tick(int nodeId, int actionId, BehaviorActivation activation, ref Context context, ref BehaviorBudget budget)
    { context.Ticks++; return BehaviorStatus.Running; }
    public void Exit(int nodeId, int actionId, BehaviorActivation activation, BehaviorStatus result, ref Context context) { }
    public void Abort(int nodeId, int actionId, BehaviorActivation activation, ref Context context) { }
}

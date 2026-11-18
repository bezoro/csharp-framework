using BenchmarkDotNet.Attributes;
using Bezoro.StateMachines;

namespace Bezoro.Statecharts.Benchmarks;

/// <summary>Measures only explicit signal dispatch, excluding construction and application effects.</summary>
[MemoryDiagnoser]
public class StatechartBenchmarks
{
    private StatechartRunner<int> _runner = null!;
    private int _context;

    /// <summary>Gets or sets alternative states, only one of which is active.</summary>
    [Params(2, 100_000)]
    public int DeclaredStates { get; set; }

    /// <summary>Builds an immutable sparse chart and starts a single owned runner.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var states = new ChartState[DeclaredStates];
        Array.Fill(states, new ChartState(default));
        var definition = new StatechartDefinition(states, new ChartRegion[] { new(default) },
            new ChartTransition[] { new(default, default, new StateId(1)), new(new StateId(1), default, default) }, 2);
        _runner = new StatechartRunner<int>(definition);
        _runner.Start(ref _context);
    }

    /// <summary>Dispatches a signal without a matching transition.</summary>
    [Benchmark]
    public StatechartDispatchResult UnmatchedSignal() => _runner.Dispatch(new SignalId(1), ref _context);

    /// <summary>Applies a complete transition and changes which single state is active.</summary>
    [Benchmark]
    public StatechartDispatchResult Transition() => _runner.Dispatch(default, ref _context);
}

/// <summary>Measures delivery of one broadcast with one independently enabled reaction in every region.</summary>
[MemoryDiagnoser]
public class OrthogonalDispatchBenchmarks
{
    private StatechartRunner<int> _runner = null!;
    private int _context;

    /// <summary>Number of simultaneously active regions receiving the signal.</summary>
    [Params(1, 8, 32, 128, 512)]
    public int ActiveRegions { get; set; }

    /// <summary>Creates independent internal reactions and external self-transitions.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var builder = new StatechartBuilder();
        var react = builder.Signal();
        var reenter = builder.Signal();
        for (int i = 0; i < ActiveRegions; i++)
        {
            var region = builder.Region();
            var state = region.State();
            region.StartIn(state);
            builder.Transition(state, react, state, kind: ChartTransitionKind.Internal);
            builder.Transition(state, reenter, state);
        }
        _runner = new StatechartRunner<int>(builder.Build());
        _runner.Start(ref _context);
    }

    /// <summary>Dispatches to every active region without reentry.</summary>
    [Benchmark]
    public StatechartDispatchResult InternalBroadcast() => _runner.Dispatch(default, ref _context);

    /// <summary>Exits and reenters every active region.</summary>
    [Benchmark]
    public StatechartDispatchResult ExternalBroadcast() => _runner.Dispatch(new SignalId(1), ref _context);
}

using Xunit;

namespace Bezoro.BehaviorTrees.Tests;

public sealed class BudgetSlicingTests
{
    [Fact]
    public void GeneratedRunningAndReactiveTreesPreserveCallbackOrderAcrossBudgetSlices()
    {
        for (int seed = 0; seed < 1_000; seed++)
        {
            var definition = new BehaviorTreeDefinition(Generate(new Random(seed), depth: 5));
            Trace large = Execute(definition, allowance: 10_000);
            Trace single = Execute(definition, allowance: 1);
            int commonCount = Math.Min(large.Events.Count, single.Events.Count);
            Assert.True(large.Events.Take(commonCount).SequenceEqual(single.Events.Take(commonCount)),
                $"Callback order changed under budget slicing for seed {seed}.");
            if (large.Status is BehaviorStatus.Success or BehaviorStatus.Failure)
            {
                Assert.Equal(large.Status, single.Status);
                Assert.Equal(large.Events.Count, single.Events.Count);
            }
        }
    }

    private static Trace Execute(BehaviorTreeDefinition definition, int allowance)
    {
        var trace = new Trace();
        var handler = new Handler();
        var runner = new BehaviorTreeRunner<Trace>(definition);
        runner.Start();
        // Reactive trees can intentionally run forever. Compare bounded callback prefixes
        // for those trees, and require both executions to finish whenever the larger slice does.
        for (int update = 0; update < 20_000 && trace.Events.Count < 2_000 && runner.Status == BehaviorStatus.Running; update++)
        {
            var budget = new BehaviorBudget(allowance);
            runner.Tick(ref trace, handler, ref budget);
            Assert.InRange(budget.Consumed, 0, allowance);
        }
        trace.Status = runner.Status;
        int eventCount = trace.Events.Count;
        runner.Stop(ref trace, handler);
        trace.Events.RemoveRange(eventCount, trace.Events.Count - eventCount);
        return trace;
    }

    private static BehaviorNode Generate(Random random, int depth)
    {
        if (depth == 0 || random.Next(4) == 0)
            return random.Next(4) switch
            {
                0 => BehaviorNode.Action(random.Next(8)),
                1 => BehaviorNode.Condition(random.Next(6)),
                2 => BehaviorNode.Succeed,
                _ => BehaviorNode.Fail
            };

        int kind = random.Next(7);
        if (kind == 4) return BehaviorNode.Invert(Generate(random, depth - 1));
        if (kind == 5) return BehaviorNode.Repeat(Generate(random, depth - 1), random.Next(1, 4));
        if (kind == 6) return BehaviorNode.Subtree(new BehaviorTreeDefinition(Generate(random, depth - 1)));
        BehaviorNode[] children = Enumerable.Range(0, random.Next(1, 5))
            .Select(_ => Generate(random, depth - 1)).ToArray();
        return kind switch
        {
            0 => BehaviorNode.Sequence(children),
            1 => BehaviorNode.Selector(children),
            2 => BehaviorNode.ReactiveSequence(children),
            _ => BehaviorNode.ReactiveSelector(children)
        };
    }

    private sealed class Trace
    {
        internal readonly List<string> Events = new();
        internal int TotalTicks;
        internal int ActivationTicks;
        internal BehaviorStatus Status;
    }

    private sealed class Handler : IBehaviorTreeHandler<Trace>
    {
        public bool Evaluate(int predicateId, in Trace context)
        {
            bool result = (context.TotalTicks + predicateId) % 3 != 0;
            context.Events.Add($"guard:{predicateId}:{result}");
            return result;
        }

        public void Enter(int nodeId, int actionId, BehaviorActivation activation, ref Trace context)
        {
            context.ActivationTicks = 0;
            context.Events.Add($"enter:{nodeId}:{actionId}");
        }

        public BehaviorStatus Tick(int nodeId, int actionId, BehaviorActivation activation,
            ref Trace context, ref BehaviorBudget budget)
        {
            context.TotalTicks++;
            context.ActivationTicks++;
            BehaviorStatus result = context.ActivationTicks < actionId % 3 + 1
                ? BehaviorStatus.Running
                : actionId % 4 == 0 ? BehaviorStatus.Failure : BehaviorStatus.Success;
            context.Events.Add($"tick:{nodeId}:{result}");
            return result;
        }

        public void Exit(int nodeId, int actionId, BehaviorActivation activation, BehaviorStatus result, ref Trace context)
            => context.Events.Add($"exit:{nodeId}:{result}");

        public void Abort(int nodeId, int actionId, BehaviorActivation activation, ref Trace context)
            => context.Events.Add($"abort:{nodeId}");
    }
}

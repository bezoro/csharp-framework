using Bezoro.Conditions;
using Bezoro.StateMachines;
using Xunit;

namespace Bezoro.Statecharts.Tests;

public sealed class StatechartOracleTests
{
    private const int RuleCount = 60;
    private static readonly int[] RegionOf = { 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6 };
    private static readonly int[] Parent = { -1, -1, 0, 0, 4, 1, 7 };
    private static readonly int[] Initial = { 0, 2, 4, 6, 8, 10, 12 };
    private static readonly int[] Order = ComputeDeclarationOrder();

    [Fact]
    public void GeneratedGuardedTransitionsMatchAnAncestryBasedConfigurationModel()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var random = new Random(seed);
            List<Rule> rules = GenerateRules(random);
            ChartTransition[] transitions = rules.Select((rule, index) => new ChartTransition(
                new StateId(rule.Source), new SignalId(rule.Signal), new StateId(rule.Target),
                CompileGuard(index, rule.Guard), rule.Priority, rule.Kind)).ToArray();
            var definition = new StatechartDefinition(
                RegionOf.Select(region => new ChartState(new RegionId(region))).ToArray(),
                Initial.Select((state, region) => new ChartRegion(new StateId(state),
                    Parent[region] < 0 ? null : new StateId(Parent[region]))).ToArray(), transitions, 3);
            var handler = new Handler();
            var runner = new StatechartRunner<Context>(definition, handler);
            var context = new Context();
            runner.Start(ref context);
            var active = new HashSet<int>();
            EnterRegion(0, -1, active, null);
            EnterRegion(1, -1, active, null);

            for (int step = 0; step < 100; step++)
            {
                context.Mask = random.Next();
                int signal = random.Next(3);
                handler.Events.Clear();
                var expectedEvents = new List<string>();
                int expectedTransitions = AdvanceModel(rules, signal, context.Mask, active, expectedEvents);

                StatechartDispatchResult actual = runner.Dispatch(new SignalId(signal), ref context);
                Assert.Equal(expectedTransitions, actual.TransitionCount);
                Assert.True(active.OrderBy(state => Order[state]).SequenceEqual(runner.ActiveStates.ToArray().Select(state => state.Value)),
                    $"Active configuration differs for seed {seed}, step {step}.");
                Assert.True(expectedEvents.SequenceEqual(handler.Events),
                    $"Guard or lifecycle order differs for seed {seed}, step {step}.");
            }
            runner.Stop(ref context);
        }
    }

    private static int AdvanceModel(List<Rule> rules, int signal, int mask, HashSet<int> active, List<string> events)
    {
        var selected = new List<int>();
        var decidedSources = new HashSet<int>();
        // This model scans every declaration and walks ancestry. It uses no compiled
        // ranks, source offsets, candidate heap, exit intervals, or runner metadata.
        IEnumerable<int> candidates = Enumerable.Range(0, rules.Count)
            .Where(index => rules[index].Signal == signal && active.Contains(rules[index].Source))
            .OrderByDescending(index => rules[index].Priority)
            .ThenByDescending(index => Depth(rules[index].Source))
            .ThenBy(index => index);
        foreach (int candidate in candidates)
        {
            Rule rule = rules[candidate];
            if (decidedSources.Contains(rule.Source) || selected.Any(index =>
                Related(rules[index].Source, rule.Source) || InDomain(rule.Source, rules[index].Domain))) continue;
            if (!EvaluateGuard(candidate, rule.Guard, mask, active, events)) continue;
            decidedSources.Add(rule.Source);
            bool conflict = selected.Any(index =>
                Related(rules[index].Source, rule.Source) ||
                active.Any(state => InDomain(state, rule.Domain) && InDomain(state, rules[index].Domain)) ||
                InDomain(rule.Source, rules[index].Domain) || InDomain(rules[index].Source, rule.Domain));
            if (!conflict) selected.Add(candidate);
        }

        int[] exited = active.Where(state => selected.Any(index => InDomain(state, rules[index].Domain)))
            .OrderByDescending(state => Order[state]).ToArray();
        foreach (int state in exited)
        {
            events.Add($"exit:{state}");
            active.Remove(state);
        }
        foreach (int index in selected) events.Add($"effect:{index}");
        foreach (int index in selected.Where(index => rules[index].Domain >= 0)
            .OrderBy(index => MinimumOrder(rules[index].Domain)))
            EnterRegion(rules[index].Domain, rules[index].Target, active, events);
        return selected.Count;
    }

    private static List<Rule> GenerateRules(Random random)
    {
        var rules = new List<Rule>();
        while (rules.Count < RuleCount)
        {
            int source = random.Next(RegionOf.Length);
            int target = random.Next(RegionOf.Length);
            var kind = (ChartTransitionKind)random.Next(3);
            if (kind == ChartTransitionKind.Internal) target = source;
            if (!TryDomain(source, target, kind, out int domain)) continue;
            rules.Add(new Rule(source, target, random.Next(3), random.Next(-3, 4), kind, domain, rules.Count % 4));
        }
        return rules;
    }

    private static int ParentOf(int state) => Parent[RegionOf[state]];

    private static bool Ancestor(int ancestor, int state)
    {
        for (int cursor = state; cursor >= 0; cursor = ParentOf(cursor))
            if (cursor == ancestor) return true;
        return false;
    }

    private static bool Related(int first, int second) => Ancestor(first, second) || Ancestor(second, first);

    private static int Depth(int state)
    {
        int depth = 0;
        for (int cursor = state; cursor >= 0; cursor = ParentOf(cursor)) depth++;
        return depth;
    }

    private static bool InDomain(int state, int region)
    {
        if (region < 0) return false;
        for (int cursor = state; cursor >= 0; cursor = ParentOf(cursor))
            if (RegionOf[cursor] == region) return true;
        return false;
    }

    private static bool TryDomain(int source, int target, ChartTransitionKind kind, out int region)
    {
        region = -1;
        if (kind == ChartTransitionKind.Internal) return source == target;
        if (kind == ChartTransitionKind.Local)
        {
            if (source == target || !Ancestor(source, target)) return false;
            for (int cursor = target; cursor >= 0; cursor = ParentOf(cursor))
                if (ParentOf(cursor) == source) { region = RegionOf[cursor]; return true; }
            return false;
        }
        if (Ancestor(source, target)) { region = RegionOf[source]; return true; }
        if (Ancestor(target, source)) { region = RegionOf[target]; return true; }

        var ancestors = new HashSet<int>();
        for (int cursor = source; cursor >= 0; cursor = ParentOf(cursor)) ancestors.Add(cursor);
        int common = -1;
        for (int cursor = target; cursor >= 0; cursor = ParentOf(cursor))
            if (ancestors.Contains(cursor)) { common = cursor; break; }
        int left = source;
        int right = target;
        while (ParentOf(left) != common) left = ParentOf(left);
        while (ParentOf(right) != common) right = ParentOf(right);
        if (RegionOf[left] != RegionOf[right]) return false;
        region = RegionOf[left];
        return true;
    }

    private static void EnterRegion(int region, int target, HashSet<int> active, List<string>? events)
    {
        int selected = Initial[region];
        if (target >= 0)
            for (int cursor = target; cursor >= 0; cursor = ParentOf(cursor))
                if (RegionOf[cursor] == region) { selected = cursor; break; }
        Assert.True(active.Add(selected));
        events?.Add($"enter:{selected}");
        for (int child = 0; child < Parent.Length; child++)
            if (Parent[child] == selected) EnterRegion(child, target, active, events);
    }

    private static int[] ComputeDeclarationOrder()
    {
        var order = new int[RegionOf.Length];
        int next = 0;
        void VisitRegion(int region)
        {
            for (int state = 0; state < RegionOf.Length; state++)
            {
                if (RegionOf[state] != region) continue;
                order[state] = next++;
                for (int child = 0; child < Parent.Length; child++)
                    if (Parent[child] == state) VisitRegion(child);
            }
        }
        VisitRegion(0);
        VisitRegion(1);
        return order;
    }

    private static int MinimumOrder(int region) => Enumerable.Range(0, RegionOf.Length)
        .Where(state => InDomain(state, region)).Min(state => Order[state]);

    private static ConditionProgram? CompileGuard(int index, int kind) => kind switch
    {
        0 => null,
        1 => ConditionExpression.Predicate(index).Compile(),
        2 => ConditionExpression.All(ConditionExpression.Predicate(index), ConditionExpression.Predicate(index + RuleCount)).Compile(),
        _ => ConditionExpression.Choose(ConditionExpression.Predicate(index),
            ConditionExpression.Predicate(index + RuleCount), ConditionExpression.Predicate(index + 2 * RuleCount)).Compile()
    };

    private static bool EvaluateGuard(int index, int kind, int mask, HashSet<int> active, List<string> events) => kind switch
    {
        0 => true,
        1 => Predicate(index, mask, active, events),
        2 => Predicate(index, mask, active, events) && Predicate(index + RuleCount, mask, active, events),
        _ => Predicate(index, mask, active, events)
            ? Predicate(index + RuleCount, mask, active, events)
            : Predicate(index + 2 * RuleCount, mask, active, events)
    };

    private static bool Predicate(int index, int mask, HashSet<int> active, List<string> events)
    {
        events.Add($"guard:{index}");
        return ((mask & (1 << (index % 30))) != 0) ^ active.Contains(index % RegionOf.Length);
    }

    private sealed record Rule(int Source, int Target, int Signal, int Priority, ChartTransitionKind Kind, int Domain, int Guard);
    private sealed class Context { internal int Mask; }

    private sealed class Handler : StatechartHandler<Context>
    {
        internal readonly List<string> Events = new();

        public override bool EvaluateGuard(int predicateId, StatechartView configuration, in Context context)
        {
            Events.Add($"guard:{predicateId}");
            return ((context.Mask & (1 << (predicateId % 30))) != 0) ^ configuration.IsActive(new StateId(predicateId % RegionOf.Length));
        }

        public override void OnEnter(StateId state, StateActivation activation, ref Context context)
            => Events.Add($"enter:{state.Value}");

        public override void OnExit(StateId state, StateActivation activation, ref Context context)
            => Events.Add($"exit:{state.Value}");

        public override void OnTransition(int transitionIndex, in ChartTransition transition, ref Context context)
            => Events.Add($"effect:{transitionIndex}");
    }
}

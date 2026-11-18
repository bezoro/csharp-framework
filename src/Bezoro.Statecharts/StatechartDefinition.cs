using Bezoro.StateMachines;

namespace Bezoro.Statecharts;

/// <summary>Compiles an immutable hierarchy, orthogonal regions, and sparse signal transitions.</summary>
/// <remarks>
/// States and regions are identified by their zero-based positions in the input spans.
/// Construction copies input. Runtime storage is bounded by maximum simultaneous activity, not total declarations.
/// </remarks>
public sealed class StatechartDefinition
{
    private readonly ChartState[] _states;
    private readonly ChartRegion[] _regions;
    private readonly ChartTransition[] _transitions;
    private readonly int[] _ranks;
    private readonly int[] _depths;
    private readonly int[] _regionStarts;
    private readonly int[] _regionEnds;
    private readonly int[] _childRegionOffsets;
    private readonly int[] _childRegions;
    private readonly int[] _transitionOrder;
    private readonly int[] _sourceOffsets;
    private readonly int[] _domains;
    private readonly StateId[] _initialStates;

    /// <summary>Copies and validates all declarations before exposing the compiled definition.</summary>
    /// <exception cref="ArgumentException">Hierarchy, initial states, transition domains, or identifiers are invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The signal count is negative.</exception>
    public StatechartDefinition(ReadOnlySpan<ChartState> states, ReadOnlySpan<ChartRegion> regions,
        ReadOnlySpan<ChartTransition> transitions, int signalCount)
    {
        if (states.IsEmpty) throw new ArgumentException("A chart needs at least one state.", nameof(states));
        if (regions.IsEmpty) throw new ArgumentException("A chart needs at least one region.", nameof(regions));
        if (signalCount < 0) throw new ArgumentOutOfRangeException(nameof(signalCount));
        _states = states.ToArray();
        _regions = regions.ToArray();
        _transitions = transitions.ToArray();
        SignalCount = signalCount;

        var regionSizes = new int[regions.Length];
        for (int state = 0; state < states.Length; state++)
        {
            int region = states[state].Region.Value;
            if (region >= regions.Length) throw new ArgumentException("A state refers to an unknown region.", nameof(states));
            regionSizes[region]++;
        }
        int rootCount = 0;
        _childRegionOffsets = new int[states.Length + 1];
        for (int region = 0; region < regions.Length; region++)
        {
            var item = regions[region];
            if (regionSizes[region] == 0 || item.InitialState.Value >= states.Length ||
                states[item.InitialState.Value].Region.Value != region)
                throw new ArgumentException("Every region needs an initial state belonging to that region.", nameof(regions));
            if (item.ParentState.HasValue)
            {
                int parent = item.ParentState.Value.Value;
                if (parent >= states.Length) throw new ArgumentException("A region refers to an unknown parent.", nameof(regions));
                _childRegionOffsets[parent + 1]++;
            }
            else rootCount++;
        }
        if (rootCount == 0) throw new ArgumentException("A chart needs a root region.", nameof(regions));
        _depths = new int[states.Length];
        var colors = new byte[states.Length];
        var work = new int[states.Length];
        for (int start = 0; start < states.Length; start++)
        {
            if (colors[start] == 2) continue;
            int count = 0;
            int cursor = start;
            while (cursor >= 0 && colors[cursor] == 0)
            {
                colors[cursor] = 1;
                work[count++] = cursor;
                cursor = ParentOf(cursor);
            }
            if (cursor >= 0 && colors[cursor] == 1)
                throw new ArgumentException("State and region ancestry contains a cycle.", nameof(regions));
            int depth = cursor < 0 ? -1 : _depths[cursor];
            while (count > 0)
            {
                int state = work[--count];
                _depths[state] = ++depth;
                colors[state] = 2;
                MaximumDepth = Math.Max(MaximumDepth, depth + 1);
            }
        }

        for (int i = 1; i < _childRegionOffsets.Length; i++) _childRegionOffsets[i] += _childRegionOffsets[i - 1];
        _childRegions = new int[regions.Length - rootCount];
        var regionCursor = (int[])_childRegionOffsets.Clone();
        var roots = new int[rootCount];
        rootCount = 0;
        for (int region = 0; region < regions.Length; region++)
        {
            if (regions[region].ParentState.HasValue)
                _childRegions[regionCursor[regions[region].ParentState!.Value.Value]++] = region;
            else roots[rootCount++] = region;
        }

        var regionOffsets = new int[regions.Length + 1];
        for (int i = 0; i < regionSizes.Length; i++) regionOffsets[i + 1] = regionOffsets[i] + regionSizes[i];
        var statesByRegion = new int[states.Length];
        var stateCursor = (int[])regionOffsets.Clone();
        for (int state = 0; state < states.Length; state++) statesByRegion[stateCursor[states[state].Region.Value]++] = state;
        _ranks = new int[states.Length];
        var byRank = new int[states.Length];
        int pending = 0;
        for (int root = roots.Length - 1; root >= 0; root--)
            for (int j = regionOffsets[roots[root] + 1] - 1; j >= regionOffsets[roots[root]]; j--)
                work[pending++] = statesByRegion[j];
        int rank = 0;
        while (pending > 0)
        {
            int state = work[--pending];
            _ranks[state] = rank;
            byRank[rank++] = state;
            for (int child = _childRegionOffsets[state + 1] - 1; child >= _childRegionOffsets[state]; child--)
            {
                int region = _childRegions[child];
                for (int j = regionOffsets[region + 1] - 1; j >= regionOffsets[region]; j--)
                    work[pending++] = statesByRegion[j];
            }
        }
        if (rank != states.Length) throw new ArgumentException("Some states do not belong to the root hierarchy.", nameof(states));

        _regionStarts = new int[regions.Length];
        Array.Fill(_regionStarts, int.MaxValue);
        _regionEnds = new int[regions.Length];
        var regionMaximum = new int[regions.Length];
        for (int i = byRank.Length - 1; i >= 0; i--)
        {
            int state = byRank[i];
            int maximum = 1;
            int end = _ranks[state] + 1;
            for (int child = _childRegionOffsets[state]; child < _childRegionOffsets[state + 1]; child++)
            {
                int region = _childRegions[child];
                maximum += regionMaximum[region];
                end = Math.Max(end, _regionEnds[region]);
            }
            int ownRegion = states[state].Region.Value;
            _regionStarts[ownRegion] = Math.Min(_regionStarts[ownRegion], _ranks[state]);
            _regionEnds[ownRegion] = Math.Max(_regionEnds[ownRegion], end);
            regionMaximum[ownRegion] = Math.Max(regionMaximum[ownRegion], maximum);
        }
        int maximumActive = 0;
        foreach (int root in roots) maximumActive += regionMaximum[root];
        MaximumActiveStates = maximumActive;

        pending = 0;
        for (int i = roots.Length - 1; i >= 0; i--) work[pending++] = roots[i];
        int initialCount = 0;
        while (pending > 0)
        {
            int state = regions[work[--pending]].InitialState.Value;
            initialCount++;
            for (int child = _childRegionOffsets[state + 1] - 1; child >= _childRegionOffsets[state]; child--)
                work[pending++] = _childRegions[child];
        }
        _initialStates = new StateId[initialCount];
        pending = 0;
        for (int i = roots.Length - 1; i >= 0; i--) work[pending++] = roots[i];
        int initialIndex = 0;
        while (pending > 0)
        {
            int state = regions[work[--pending]].InitialState.Value;
            _initialStates[initialIndex++] = new StateId(state);
            for (int child = _childRegionOffsets[state + 1] - 1; child >= _childRegionOffsets[state]; child--)
                work[pending++] = _childRegions[child];
        }

        _domains = transitions.IsEmpty ? Array.Empty<int>() : new int[transitions.Length];
        _transitionOrder = transitions.IsEmpty ? Array.Empty<int>() : new int[transitions.Length];
        _sourceOffsets = transitions.IsEmpty ? Array.Empty<int>() : new int[states.Length + 1];
        for (int i = 0; i < transitions.Length; i++)
        {
            var transition = transitions[i];
            if (transition.Source.Value >= states.Length || transition.Target.Value >= states.Length || transition.Signal.Value >= signalCount)
                throw new ArgumentException("A transition refers outside the chart.", nameof(transitions));
            if (transition.Kind is < ChartTransitionKind.External or > ChartTransitionKind.Internal)
                throw new ArgumentException("Unknown transition kind.", nameof(transitions));
            _domains[i] = ComputeDomain(transition);
            // A source leaf occupies exactly one slot in its own region. Internal transitions
            // leave that slot intact; exits rooted at that region can remove only that leaf
            // from the prior configuration. Other simultaneously active leaves are disjoint.
            if (_childRegionOffsets[transition.Source.Value] != _childRegionOffsets[transition.Source.Value + 1] ||
                (_domains[i] >= 0 && _domains[i] != _states[transition.Source.Value].Region.Value))
                HasIndependentSources = false;
            HasConfigurationTransitions |= transition.Kind != ChartTransitionKind.Internal;
            _transitionOrder[i] = i;
            _sourceOffsets[transition.Source.Value + 1]++;
            GuardStackSize = Math.Max(GuardStackSize, transition.Guard?.RequiredStackSize ?? 0);
        }
        if (!transitions.IsEmpty)
        {
            Array.Sort(_transitionOrder, new TransitionOrder(_transitions));
            for (int i = 1; i < _sourceOffsets.Length; i++)
            {
                if (_sourceOffsets[i] != 0) TransitionSourceCount++;
                _sourceOffsets[i] += _sourceOffsets[i - 1];
            }
        }
    }

    /// <summary>Gets immutable state declarations, indexed by StateId.</summary>
    public ReadOnlySpan<ChartState> States => _states;
    /// <summary>Gets immutable region declarations, indexed by RegionId.</summary>
    public ReadOnlySpan<ChartRegion> Regions => _regions;
    /// <summary>Gets transitions in declaration order.</summary>
    public ReadOnlySpan<ChartTransition> Transitions => _transitions;
    /// <summary>Gets the state count.</summary>
    public int StateCount => _states.Length;
    /// <summary>Gets the region count.</summary>
    public int RegionCount => _regions.Length;
    /// <summary>Gets the accepted signal count. Zero supports charts without input.</summary>
    public int SignalCount { get; }
    /// <summary>Gets the maximum number of simultaneously active states over all configurations.</summary>
    public int MaximumActiveStates { get; }
    /// <summary>Gets maximum ancestry length, including a root state.</summary>
    public int MaximumDepth { get; private set; }
    /// <summary>Gets the maximum Boolean scratch required by any guard.</summary>
    public int GuardStackSize { get; private set; }
    /// <summary>Gets the number of distinct states declaring outgoing transitions.</summary>
    public int TransitionSourceCount { get; private set; }
    /// <summary>Gets whether any transition can change active state configuration.</summary>
    public bool HasConfigurationTransitions { get; private set; }

    internal bool HasIndependentSources { get; } = true;
    internal int DomainEnd(int transition) => _regionEnds[_domains[transition]];
    internal ReadOnlySpan<StateId> InitialStates => _initialStates;
    internal int Rank(StateId state) => _ranks[state.Value];
    internal int Depth(StateId state) => _depths[state.Value];
    internal int Domain(int transition) => _domains[transition];
    internal int DomainStart(int transition) => _domains[transition] < 0 ? int.MaxValue : _regionStarts[_domains[transition]];
    private int ParentOf(int state) => _regions[_states[state].Region.Value].ParentState?.Value ?? -1;

    internal int FindActive(ReadOnlySpan<StateId> active, StateId state)
    {
        if (state.Value >= StateCount) return -1;
        int rank = _ranks[state.Value];
        int low = 0, high = active.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int candidate = _ranks[active[middle].Value];
            if (candidate < rank) low = middle + 1;
            else if (candidate > rank) high = middle - 1;
            else return middle;
        }
        return -1;
    }

    internal void GetSignalRange(StateId state, SignalId signal, out int first, out int end)
    {
        first = end = 0;
        if (_transitionOrder.Length == 0) return;
        int low = _sourceOffsets[state.Value], limit = _sourceOffsets[state.Value + 1], high = limit;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_transitions[_transitionOrder[middle]].Signal.Value < signal.Value) low = middle + 1;
            else high = middle;
        }
        first = low;
        high = limit;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_transitions[_transitionOrder[middle]].Signal.Value <= signal.Value) low = middle + 1;
            else high = middle;
        }
        end = low;
    }
    internal int OrderedTransition(int index) => _transitionOrder[index];
    internal bool HasFollowingGuard(int orderedIndex)
    {
        if (orderedIndex + 1 >= _transitionOrder.Length) return false;
        var current = _transitions[_transitionOrder[orderedIndex]];
        var next = _transitions[_transitionOrder[orderedIndex + 1]];
        return current.Source == next.Source && current.Signal == next.Signal;
    }
    internal bool SuppressesSource(int selectedTransition, StateId source)
    {
        StateId selectedSource = _transitions[selectedTransition].Source;
        return IsAncestor(selectedSource.Value, source.Value) || IsAncestor(source.Value, selectedSource.Value) ||
            IsInDomain(source, _domains[selectedTransition]);
    }
    internal bool IsInDomain(StateId state, int region) =>
        region >= 0 && _ranks[state.Value] >= _regionStarts[region] && _ranks[state.Value] < _regionEnds[region];

    internal bool Conflicts(int first, int second)
    {
        StateId left = _transitions[first].Source, right = _transitions[second].Source;
        if (IsAncestor(left.Value, right.Value) || IsAncestor(right.Value, left.Value)) return true;
        int a = _domains[first], b = _domains[second];
        return IsInDomain(left, b) || IsInDomain(right, a) ||
            (a >= 0 && b >= 0 && _regionStarts[a] < _regionEnds[b] && _regionStarts[b] < _regionEnds[a]);
    }
    private bool IsAncestor(int ancestor, int state)
    {
        int start = _ranks[ancestor];
        int childEnd = _childRegionOffsets[ancestor + 1];
        int end = childEnd == _childRegionOffsets[ancestor] ? start + 1 : _regionEnds[_childRegions[childEnd - 1]];
        return _ranks[state] >= start && _ranks[state] < end;
    }
    internal int CompareCandidates(int first, int second)
    {
        int priority = _transitions[second].Priority.CompareTo(_transitions[first].Priority);
        if (priority != 0) return priority;
        int depth = Depth(_transitions[second].Source).CompareTo(Depth(_transitions[first].Source));
        return depth != 0 ? depth : first.CompareTo(second);
    }

    internal int AppendEntries(int transitionIndex, StateId[] destination, int count, int[] regionStack, StateId[] path)
    {
        int domain = _domains[transitionIndex];
        if (domain < 0) return count;
        int cursor = _transitions[transitionIndex].Target.Value;
        int pathCount = 0;
        while (true)
        {
            path[pathCount++] = new StateId(cursor);
            if (_states[cursor].Region.Value == domain) break;
            cursor = ParentOf(cursor);
        }
        int pathNext = pathCount - 1;
        int pending = 1;
        regionStack[0] = domain;
        while (pending > 0)
        {
            int region = regionStack[--pending];
            StateId state = pathNext >= 0 && _states[path[pathNext].Value].Region.Value == region
                ? path[pathNext--] : _regions[region].InitialState;
            destination[count++] = state;
            for (int child = _childRegionOffsets[state.Value + 1] - 1; child >= _childRegionOffsets[state.Value]; child--)
                regionStack[pending++] = _childRegions[child];
        }
        return count;
    }

    private int ComputeDomain(ChartTransition transition)
    {
        int source = transition.Source.Value, target = transition.Target.Value;
        if (transition.Kind == ChartTransitionKind.Internal)
        {
            if (source != target) throw new ArgumentException("Internal transitions require identical source and target.", nameof(transition));
            return -1;
        }
        if (transition.Kind == ChartTransitionKind.Local)
        {
            if (source == target || !IsAncestor(source, target))
                throw new ArgumentException("Local transitions require a strict descendant target.", nameof(transition));
            while (ParentOf(target) != source) target = ParentOf(target);
            return _states[target].Region.Value;
        }
        int a = source, b = target;
        while (a >= 0 && b >= 0 && _depths[a] > _depths[b]) a = ParentOf(a);
        while (a >= 0 && b >= 0 && _depths[b] > _depths[a]) b = ParentOf(b);
        while (a != b) { a = ParentOf(a); b = ParentOf(b); }
        int ancestor = a;
        if (ancestor == source || ancestor == target) return _states[ancestor].Region.Value;
        while (ParentOf(source) != ancestor) source = ParentOf(source);
        while (ParentOf(target) != ancestor) target = ParentOf(target);
        int sourceRegion = _states[source].Region.Value;
        if (_states[target].Region.Value != sourceRegion)
            throw new ArgumentException("Transitions cannot jump directly between orthogonal regions.", nameof(transition));
        return sourceRegion;
    }

    private sealed class TransitionOrder : IComparer<int>
    {
        private readonly ChartTransition[] _values;
        internal TransitionOrder(ChartTransition[] values) => _values = values;
        public int Compare(int x, int y)
        {
            int source = _values[x].Source.Value.CompareTo(_values[y].Source.Value);
            if (source != 0) return source;
            int signal = _values[x].Signal.Value.CompareTo(_values[y].Signal.Value);
            if (signal != 0) return signal;
            int priority = _values[y].Priority.CompareTo(_values[x].Priority);
            return priority != 0 ? priority : x.CompareTo(y);
        }
    }
}

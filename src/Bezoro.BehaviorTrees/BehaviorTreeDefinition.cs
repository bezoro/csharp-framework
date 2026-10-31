using Bezoro.Conditions;

namespace Bezoro.BehaviorTrees;

/// <summary>Compiles a construction expression into an immutable preorder control-flow table.</summary>
/// <remarks>Reused construction nodes and subtrees become separate occurrences. Runners share this definition.</remarks>
public sealed class BehaviorTreeDefinition
{
    internal readonly NodeData[] Nodes;
    internal readonly ConditionProgram[] Programs;
    /// <summary>Copies and validates a tree without recursive compilation.</summary>
    public BehaviorTreeDefinition(BehaviorNode root, int maximumNodes = 16_384, int maximumDepth = 256)
        : this(root, maximumNodes, maximumDepth, null) { }

    internal BehaviorTreeDefinition(BehaviorNode root, int maximumNodes, int maximumDepth,
        Dictionary<BehaviorNode, int>? actionOccurrences)
    {
        if (root is null) throw new ArgumentNullException(nameof(root));
        if (maximumNodes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        if (maximumDepth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        var nodes = new List<NodeData>(Math.Min(maximumNodes, 16));
        List<ConditionProgram>? programs = null;
        Dictionary<ConditionProgram, int>? programIndices = null;
        var ancestors = new HashSet<BehaviorNode>();
        var stack = new Stack<CompileFrame>();
        stack.Push(new CompileFrame(root, null, 0, 1, false));
        while (stack.Count != 0)
        {
            var frame = stack.Pop();
            if (frame.OutputIndex < 0)
            {
                if (nodes.Count == maximumNodes) throw new ArgumentException("The expanded tree exceeds the node budget.", nameof(root));
                if (frame.Depth > maximumDepth) throw new ArgumentException("The expanded tree exceeds the depth budget.", nameof(root));
                MaximumDepth = Math.Max(MaximumDepth, frame.Depth);
                NodeData data;
                ConditionProgram? program;
                if (frame.Source is not null)
                {
                    if (!ancestors.Add(frame.Source)) throw new ArgumentException("The construction graph contains a cycle.", nameof(root));
                    data = new NodeData(frame.Source.Kind, frame.Source.Parameter,
                        frame.Source.Kind == BehaviorNodeKind.Subtree ? 1 : frame.Source.Children.Length);
                    program = frame.Source.Program;
                }
                else
                {
                    data = frame.Copy!.Nodes[frame.SourceIndex];
                    program = data.Kind == BehaviorNodeKind.Condition && data.Parameter < 0 ? frame.Copy.Programs[~data.Parameter] : null;
                    data.EndIndex = -1;
                }
                if (data.ChildCount > maximumNodes - nodes.Count - 1)
                    throw new ArgumentException("The expanded tree exceeds the node budget.", nameof(root));
                if (program is not null)
                {
                    programs ??= new List<ConditionProgram>();
                    programIndices ??= new Dictionary<ConditionProgram, int>();
                    if (!programIndices.TryGetValue(program, out int programIndex))
                    {
                        programIndex = programs.Count;
                        programs.Add(program);
                        programIndices.Add(program, programIndex);
                    }
                    data.Parameter = ~programIndex;
                    RequiredConditionStackSize = Math.Max(RequiredConditionStackSize, program.RequiredStackSize);
                }
                bool reactive = data.Kind is BehaviorNodeKind.ReactiveSequence or BehaviorNodeKind.ReactiveSelector;
                if (reactive) HasReactiveNodes = true;
                frame.ReactivePath |= reactive;
                if (frame.ReactivePath && data.Kind == BehaviorNodeKind.Action)
                    MaximumReactiveDepth = Math.Max(MaximumReactiveDepth, frame.Depth);
                frame.OutputIndex = nodes.Count;
                if (frame.Source is not null && data.Kind == BehaviorNodeKind.Action && actionOccurrences is not null)
                {
                    // A reused expression denotes multiple occurrences. Never silently bind the first one.
                    if (actionOccurrences.ContainsKey(frame.Source)) actionOccurrences[frame.Source] = -1;
                    else actionOccurrences.Add(frame.Source, frame.OutputIndex);
                }
                nodes.Add(data);
            }
            var current = nodes[frame.OutputIndex];
            if (frame.NextChild < current.ChildCount)
            {
                CompileFrame child;
                if (frame.Source is not null)
                {
                    child = frame.Source.Kind == BehaviorNodeKind.Subtree
                        ? new CompileFrame(null, frame.Source.SubtreeDefinition, 0, frame.Depth + 1, frame.ReactivePath)
                        : new CompileFrame(frame.Source.Children[frame.NextChild], null, 0, frame.Depth + 1, frame.ReactivePath);
                }
                else
                {
                    int childIndex = frame.NextCopyChild;
                    frame.NextCopyChild = frame.Copy!.Nodes[childIndex].EndIndex + 1;
                    child = new CompileFrame(null, frame.Copy, childIndex, frame.Depth + 1, frame.ReactivePath);
                }
                frame.NextChild++;
                stack.Push(frame);
                stack.Push(child);
            }
            else
            {
                current.EndIndex = nodes.Count - 1;
                nodes[frame.OutputIndex] = current;
                if (frame.Source is not null) ancestors.Remove(frame.Source);
            }
        }
        Nodes = nodes.ToArray();
        Programs = programs?.ToArray() ?? Array.Empty<ConditionProgram>();
    }
    /// <summary>Gets the expanded occurrence count.</summary>
    public int NodeCount => Nodes.Length;
    /// <summary>Gets the actual maximum path depth, which bounds each runner's frame storage.</summary>
    public int MaximumDepth { get; }
    /// <summary>Gets whether the definition contains reactive control nodes.</summary>
    public bool HasReactiveNodes { get; }
    /// <summary>Gets the deepest action path containing a reactive ancestor; zero needs no reactive snapshot.</summary>
    public int MaximumReactiveDepth { get; }
    /// <summary>Gets the largest logical-program scratch requirement; zero needs no scratch array.</summary>
    public int RequiredConditionStackSize { get; }
    /// <summary>Gets one compiled occurrence by its zero-based node ID.</summary>
    public BehaviorNodeInfo GetNode(int nodeId)
    {
        if (nodeId < 0 || nodeId >= Nodes.Length) throw new ArgumentOutOfRangeException(nameof(nodeId));
        var node = Nodes[nodeId];
        return new(node.Kind, node.Kind == BehaviorNodeKind.Condition && node.Parameter < 0 ? -1 : node.Parameter, node.ChildCount, node.EndIndex);
    }
    internal struct NodeData
    {
        internal BehaviorNodeKind Kind;
        internal int Parameter;
        internal int ChildCount;
        internal int EndIndex;
        internal NodeData(BehaviorNodeKind kind, int parameter, int childCount)
        { Kind = kind; Parameter = parameter; ChildCount = childCount; EndIndex = -1; }
    }
    private struct CompileFrame
    {
        internal BehaviorNode? Source;
        internal BehaviorTreeDefinition? Copy;
        internal int SourceIndex;
        internal int Depth;
        internal int OutputIndex;
        internal int NextChild;
        internal int NextCopyChild;
        internal bool ReactivePath;
        internal CompileFrame(BehaviorNode? source, BehaviorTreeDefinition? copy, int index, int depth, bool reactivePath)
        { Source = source; Copy = copy; SourceIndex = index; Depth = depth; OutputIndex = -1; NextChild = 0; NextCopyChild = index + 1; ReactivePath = reactivePath; }
    }
}

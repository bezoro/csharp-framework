namespace Bezoro.Progression;

/// <summary>Shares an immutable, acyclic story graph across players.</summary>
public sealed class StoryCatalog
{
    private readonly StoryStageDefinition[] _stages;
    private readonly Dictionary<ProgressionId, int> _lookup;
    private readonly Requirement[][] _requirements;

    /// <summary>Copies stages and validates unique identifiers, existing dependencies, reachable star requirements, and absence of cycles.</summary>
    /// <remarks>Input order does not need to be topological. Validation is iterative to support deep campaigns without stack recursion.</remarks>
    public StoryCatalog(ReadOnlySpan<StoryStageDefinition> stages)
    {
        _stages = stages.ToArray();
        _lookup = new Dictionary<ProgressionId, int>(stages.Length);
        _requirements = stages.Length == 0 ? Array.Empty<Requirement[]>() : new Requirement[stages.Length][];
        for (int index = 0; index < _stages.Length; index++)
        {
            StoryStageDefinition stage = _stages[index] ?? throw new ArgumentException("Stages cannot be null.", nameof(stages));
            if (_lookup.ContainsKey(stage.Id)) throw new ArgumentException("Stage identifiers must be unique.", nameof(stages));
            _lookup.Add(stage.Id, index);
        }
        var dependents = new List<int>?[_stages.Length];
        var incoming = new int[_stages.Length];
        var ready = new Queue<int>();
        for (int index = 0; index < _stages.Length; index++)
        {
            ReadOnlySpan<StoryPrerequisite> prerequisites = _stages[index].Prerequisites;
            var requirements = prerequisites.Length == 0 ? Array.Empty<Requirement>() : new Requirement[prerequisites.Length];
            for (int requirementIndex = 0; requirementIndex < prerequisites.Length; requirementIndex++)
            {
                StoryPrerequisite prerequisite = prerequisites[requirementIndex];
                if (!_lookup.TryGetValue(prerequisite.Stage, out int dependency))
                    throw new ArgumentException("Every prerequisite must belong to this catalog.", nameof(stages));
                if (prerequisite.RequiredStars > _stages[dependency].MaximumStars)
                    throw new ArgumentException("A prerequisite cannot require more stars than its stage supports.", nameof(stages));
                requirements[requirementIndex] = new Requirement(dependency, prerequisite.RequiredStars);
                (dependents[dependency] ??= new List<int>()).Add(index);
            }
            _requirements[index] = requirements;
            incoming[index] = requirements.Length;
            if (requirements.Length == 0) ready.Enqueue(index);
        }
        int visited = 0;
        while (ready.Count != 0)
        {
            int stageIndex = ready.Dequeue();
            visited++;
            if (dependents[stageIndex] is not { } children) continue;
            foreach (int child in children)
            {
                incoming[child]--;
                if (incoming[child] == 0) ready.Enqueue(child);
            }
        }
        if (visited != _stages.Length) throw new ArgumentException("Story prerequisites cannot contain a cycle.", nameof(stages));
    }

    /// <summary>Gets the number of stages.</summary>
    public int Count => _stages.Length;
    /// <summary>Gets immutable stages in input order.</summary>
    public ReadOnlySpan<StoryStageDefinition> Stages => _stages;
    /// <summary>Gets a stage definition, or throws for an unknown identifier.</summary>
    public StoryStageDefinition GetDefinition(ProgressionId stage) => _stages[GetIndex(stage)];

    internal int GetIndex(ProgressionId stage)
    {
        if (!_lookup.TryGetValue(stage, out int index))
            throw new ArgumentException("The stage does not belong to this catalog.", nameof(stage));
        return index;
    }

    internal ReadOnlySpan<Requirement> GetRequirements(int stageIndex) => _requirements[stageIndex];

    internal readonly struct Requirement
    {
        internal Requirement(int stageIndex, byte requiredStars)
        {
            StageIndex = stageIndex;
            RequiredStars = requiredStars;
        }

        internal int StageIndex { get; }
        internal byte RequiredStars { get; }
    }
}

namespace Bezoro.Progression;

/// <summary>Shares immutable quest definitions and a counter index across player progress instances.</summary>
public sealed class QuestCatalog
{
    private readonly QuestDefinition[] _definitions;
    private readonly Dictionary<ProgressionId, int> _lookup;
    private readonly Dictionary<ProgressionId, ObjectiveLink[]> _counterIndex;
    private readonly int[] _objectiveOffsets;

    /// <summary>Copies quest references in input order and rejects null or duplicate definitions.</summary>
    public QuestCatalog(ReadOnlySpan<QuestDefinition> definitions)
    {
        _definitions = definitions.ToArray();
        _lookup = new Dictionary<ProgressionId, int>(definitions.Length);
        _objectiveOffsets = definitions.Length == 0 ? Array.Empty<int>() : new int[definitions.Length];
        var links = new Dictionary<ProgressionId, List<ObjectiveLink>>();
        int totalObjectives = 0;
        for (int questIndex = 0; questIndex < _definitions.Length; questIndex++)
        {
            QuestDefinition definition = _definitions[questIndex] ?? throw new ArgumentException("Quest definitions cannot be null.", nameof(definitions));
            if (_lookup.ContainsKey(definition.Id))
                throw new ArgumentException("Quest identifiers must be unique.", nameof(definitions));
            _lookup.Add(definition.Id, questIndex);
            _objectiveOffsets[questIndex] = totalObjectives;
            for (int objectiveIndex = 0; objectiveIndex < definition.Objectives.Length; objectiveIndex++)
            {
                QuestObjective objective = definition.Objectives[objectiveIndex];
                if (!links.TryGetValue(objective.Counter, out List<ObjectiveLink>? counterLinks))
                {
                    counterLinks = new List<ObjectiveLink>();
                    links.Add(objective.Counter, counterLinks);
                }
                counterLinks.Add(new ObjectiveLink(questIndex, checked(totalObjectives + objectiveIndex), objective.Target));
            }
            totalObjectives = checked(totalObjectives + definition.Objectives.Length);
        }
        ObjectiveCount = totalObjectives;
        _counterIndex = new Dictionary<ProgressionId, ObjectiveLink[]>(links.Count);
        foreach (KeyValuePair<ProgressionId, List<ObjectiveLink>> pair in links)
            _counterIndex.Add(pair.Key, pair.Value.ToArray());
    }

    /// <summary>Gets the number of quests.</summary>
    public int Count => _definitions.Length;
    /// <summary>Gets the number of objective counters in each player's state.</summary>
    public int ObjectiveCount { get; }
    /// <summary>Gets the definitions in stable input order.</summary>
    public ReadOnlySpan<QuestDefinition> Definitions => _definitions;
    /// <summary>Gets a quest definition, or throws for an unknown identifier.</summary>
    public QuestDefinition GetDefinition(ProgressionId quest) => _definitions[GetIndex(quest)];

    internal int GetIndex(ProgressionId quest)
    {
        if (!_lookup.TryGetValue(quest, out int index))
            throw new ArgumentException("The quest does not belong to this catalog.", nameof(quest));
        return index;
    }

    internal int GetObjectiveOffset(int index) => _objectiveOffsets[index];
    internal ReadOnlySpan<ObjectiveLink> GetCounterLinks(ProgressionId counter) =>
        _counterIndex.TryGetValue(counter, out ObjectiveLink[]? links) ? links : ReadOnlySpan<ObjectiveLink>.Empty;

    internal readonly struct ObjectiveLink
    {
        internal ObjectiveLink(int questIndex, int progressIndex, long target)
        {
            QuestIndex = questIndex;
            ProgressIndex = progressIndex;
            Target = target;
        }

        internal int QuestIndex { get; }
        internal int ProgressIndex { get; }
        internal long Target { get; }
    }
}

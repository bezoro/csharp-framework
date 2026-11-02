namespace Bezoro.Progression;

/// <summary>An immutable quest whose objectives must all be completed before its rewards can be claimed.</summary>
public sealed class QuestDefinition
{
    private readonly QuestObjective[] _objectives;
    private readonly RewardGrant[] _rewards;

    /// <summary>Copies a nonempty set of unique counters and an optional reward batch.</summary>
    public QuestDefinition(ProgressionId id, ReadOnlySpan<QuestObjective> objectives, ReadOnlySpan<RewardGrant> rewards = default)
    {
        if (!id.IsValid) throw new ArgumentException("A valid quest identifier is required.", nameof(id));
        if (objectives.IsEmpty) throw new ArgumentException("A quest requires at least one objective.", nameof(objectives));
        var counters = new HashSet<ProgressionId>();
        for (int index = 0; index < objectives.Length; index++)
        {
            QuestObjective objective = objectives[index];
            if (!objective.Counter.IsValid || objective.Target <= 0 || !counters.Add(objective.Counter))
                throw new ArgumentException("Objectives must have valid unique counters and positive targets.", nameof(objectives));
        }
        Id = id;
        _objectives = objectives.ToArray();
        _rewards = RewardBatch.CopyValidated(rewards);
    }

    /// <summary>Gets the application-defined quest identifier.</summary>
    public ProgressionId Id { get; }
    /// <summary>Gets all required objectives in definition order.</summary>
    public ReadOnlySpan<QuestObjective> Objectives => _objectives;
    /// <summary>Gets the entitlements produced by a successful claim.</summary>
    public RewardBatch Rewards => new(_rewards);
}

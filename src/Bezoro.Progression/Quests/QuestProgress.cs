namespace Bezoro.Progression;

/// <summary>Owns one player's quest counters and claim state for a shared catalog.</summary>
/// <remarks>Requires one owner or external synchronization. Feed authoritative events once; this class does not deduplicate event deliveries.</remarks>
public sealed class QuestProgress
{
    private readonly long[] _progress;
    private readonly QuestStatus[] _statuses;

    /// <summary>Creates inactive quests with zero counters.</summary>
    public QuestProgress(QuestCatalog catalog)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _progress = catalog.ObjectiveCount == 0 ? Array.Empty<long>() : new long[catalog.ObjectiveCount];
        _statuses = catalog.Count == 0 ? Array.Empty<QuestStatus>() : new QuestStatus[catalog.Count];
    }

    /// <summary>Gets the immutable catalog defining this state.</summary>
    public QuestCatalog Catalog { get; }
    /// <summary>Gets a quest's current lifecycle state.</summary>
    public QuestStatus GetStatus(ProgressionId quest) => _statuses[Catalog.GetIndex(quest)];

    /// <summary>Activates an inactive quest. Returns false when it has already been activated.</summary>
    public bool TryActivate(ProgressionId quest)
    {
        int index = Catalog.GetIndex(quest);
        if (_statuses[index] != QuestStatus.Inactive) return false;
        _statuses[index] = QuestStatus.Active;
        return true;
    }

    /// <summary>Gets the bounded counter value of an objective by its index within a quest.</summary>
    public long GetObjectiveProgress(ProgressionId quest, int objectiveIndex)
    {
        int questIndex = Catalog.GetIndex(quest);
        if (objectiveIndex < 0 || objectiveIndex >= Catalog.Definitions[questIndex].Objectives.Length)
            throw new ArgumentOutOfRangeException(nameof(objectiveIndex));
        return _progress[Catalog.GetObjectiveOffset(questIndex) + objectiveIndex];
    }

    /// <summary>Records a positive event amount and returns the number of newly completed quests.</summary>
    /// <remarks>Visits only objectives indexed for this counter. Progress saturates at each target, including when the amount is Int64.MaxValue.</remarks>
    public int Record(ProgressionId counter, long amount = 1)
    {
        if (!counter.IsValid) throw new ArgumentException("A valid counter identifier is required.", nameof(counter));
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        int completed = 0;
        foreach (QuestCatalog.ObjectiveLink link in Catalog.GetCounterLinks(counter))
        {
            if (_statuses[link.QuestIndex] != QuestStatus.Active) continue;
            long current = _progress[link.ProgressIndex];
            if (current == link.Target) continue;
            long remaining = link.Target - current;
            _progress[link.ProgressIndex] = amount >= remaining ? link.Target : current + amount;
            if (_progress[link.ProgressIndex] == link.Target && AllObjectivesCompleted(link.QuestIndex, _progress))
            {
                _statuses[link.QuestIndex] = QuestStatus.Completed;
                completed++;
            }
        }
        return completed;
    }

    /// <summary>Changes a completed quest to claimed and returns its reward entitlements exactly once in this state.</summary>
    /// <remarks>Persist this state and apply the returned rewards in the same application transaction for durable exactly-once delivery.</remarks>
    public bool TryClaim(ProgressionId quest, out RewardBatch rewards)
    {
        int index = Catalog.GetIndex(quest);
        if (_statuses[index] != QuestStatus.Completed)
        {
            rewards = default;
            return false;
        }
        _statuses[index] = QuestStatus.Claimed;
        rewards = Catalog.Definitions[index].Rewards;
        return true;
    }

    /// <summary>Copies the complete state into caller-owned storage in catalog and objective order.</summary>
    public void CopyStateTo(Span<QuestStatus> statuses, Span<long> objectiveProgress)
    {
        if (statuses.Length < _statuses.Length) throw new ArgumentException("The status destination is too short.", nameof(statuses));
        if (objectiveProgress.Length < _progress.Length) throw new ArgumentException("The progress destination is too short.", nameof(objectiveProgress));
        _statuses.AsSpan().CopyTo(statuses);
        _progress.AsSpan().CopyTo(objectiveProgress);
    }

    /// <summary>Validates an entire saved state before replacing it. Invalid lengths, counters, or lifecycle combinations return false without mutation.</summary>
    /// <remarks>The application must select the matching catalog/schema and authorize restoration, which may otherwise roll back claimed rewards.</remarks>
    public bool TryRestore(ReadOnlySpan<QuestStatus> statuses, ReadOnlySpan<long> objectiveProgress)
    {
        if (statuses.Length != _statuses.Length || objectiveProgress.Length != _progress.Length) return false;
        for (int questIndex = 0; questIndex < statuses.Length; questIndex++)
        {
            QuestStatus status = statuses[questIndex];
            if ((byte)status > (byte)QuestStatus.Claimed) return false;
            ReadOnlySpan<QuestObjective> objectives = Catalog.Definitions[questIndex].Objectives;
            int offset = Catalog.GetObjectiveOffset(questIndex);
            bool allCompleted = true;
            for (int objectiveIndex = 0; objectiveIndex < objectives.Length; objectiveIndex++)
            {
                long count = objectiveProgress[offset + objectiveIndex];
                if (count < 0 || count > objectives[objectiveIndex].Target || (status == QuestStatus.Inactive && count != 0))
                    return false;
                allCompleted &= count == objectives[objectiveIndex].Target;
            }
            if ((status is QuestStatus.Completed or QuestStatus.Claimed) != allCompleted) return false;
        }
        statuses.CopyTo(_statuses);
        objectiveProgress.CopyTo(_progress);
        return true;
    }

    private bool AllObjectivesCompleted(int questIndex, ReadOnlySpan<long> progress)
    {
        ReadOnlySpan<QuestObjective> objectives = Catalog.Definitions[questIndex].Objectives;
        int offset = Catalog.GetObjectiveOffset(questIndex);
        for (int objectiveIndex = 0; objectiveIndex < objectives.Length; objectiveIndex++)
            if (progress[offset + objectiveIndex] != objectives[objectiveIndex].Target) return false;
        return true;
    }
}

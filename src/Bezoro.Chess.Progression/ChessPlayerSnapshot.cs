using Bezoro.PlayerProfiles;
using Bezoro.Progression;

namespace Bezoro.Chess.Progression;

/// <summary>One immutable quest objective projection.</summary>
public readonly struct ChessQuestObjectiveSnapshot
{
    internal ChessQuestObjectiveSnapshot(ProgressionId counter, long current, long target) { Counter = counter; Current = current; Target = target; }
    /// <summary>Gets the application event counter.</summary>
    public ProgressionId Counter { get; }
    /// <summary>Gets persisted, bounded progress.</summary>
    public long Current { get; }
    /// <summary>Gets the catalog target.</summary>
    public long Target { get; }
}

/// <summary>Immutable quest state for presentation; no counter mutation is exposed.</summary>
public sealed class ChessQuestSnapshot
{
    internal ChessQuestSnapshot(ChessQuestDefinition definition, QuestProgress progress)
    {
        Id = definition.Definition.Id; Title = definition.Title; Status = progress.GetStatus(Id);
        var objectives = new ChessQuestObjectiveSnapshot[definition.Definition.Objectives.Length];
        for (int index = 0; index < objectives.Length; index++)
        {
            var objective = definition.Definition.Objectives[index];
            objectives[index] = new(objective.Counter, progress.GetObjectiveProgress(Id, index), objective.Target);
        }
        Objectives = Array.AsReadOnly(objectives);
        Rewards = definition.Definition.Rewards;
    }
    /// <summary>Gets the stable quest identity.</summary>
    public ProgressionId Id { get; }
    /// <summary>Gets default display content.</summary>
    public string Title { get; }
    /// <summary>Gets the quest lifecycle.</summary>
    public QuestStatus Status { get; }
    /// <summary>Gets read-only objective counters.</summary>
    public IReadOnlyList<ChessQuestObjectiveSnapshot> Objectives { get; }
    /// <summary>Gets the immutable claim entitlements.</summary>
    public RewardBatch Rewards { get; }
}

/// <summary>Immutable curriculum stage progress; metadata remains in the shared campaign.</summary>
public readonly struct ChessStageSnapshot
{
    internal ChessStageSnapshot(ProgressionId id, string lessonId, bool unlocked, byte stars) { Id = id; LessonId = lessonId; IsUnlocked = unlocked; BestStars = stars; }
    /// <summary>Gets the curriculum stage identity.</summary>
    public ProgressionId Id { get; }
    /// <summary>Gets the authored lesson identity.</summary>
    public string LessonId { get; }
    /// <summary>Gets whether StoryCatalog prerequisites are satisfied.</summary>
    public bool IsUnlocked { get; }
    /// <summary>Gets one completion star, or zero before completion; no scoring policy is invented.</summary>
    public byte BestStars { get; }
}

/// <summary>A fully persisted, immutable local player view. Retained snapshots never change after later commands.</summary>
public sealed class ChessPlayerSnapshot
{
    internal ChessPlayerSnapshot(PlayerProfile profile, EloRating rating, PlayerMatchRecord[] history,
        ChessQuestSnapshot[] quests, ChessStageSnapshot[] stages, Dictionary<ProgressionId, long> rewards,
        ChessCosmeticSnapshot[] cosmetics, string boardId, string piecesId)
    {
        Profile = profile; TrainingRating = rating; History = Array.AsReadOnly(history);
        Quests = Array.AsReadOnly(quests); Stages = Array.AsReadOnly(stages);
        RewardBalances = new System.Collections.ObjectModel.ReadOnlyDictionary<ProgressionId, long>(rewards);
        Cosmetics = Array.AsReadOnly(cosmetics); EquippedBoardId = boardId; EquippedPiecesId = piecesId;
        Wins = history.Count(record => record.Outcome == PlayerMatchOutcome.Win);
        Draws = history.Count(record => record.Outcome == PlayerMatchOutcome.Draw);
        Losses = history.Length - Wins - Draws;
    }
    /// <summary>Gets local profile presentation metadata.</summary>
    public PlayerProfile Profile { get; }
    /// <summary>Gets Elo in the local training pool, never an online or global ranking.</summary>
    public EloRating TrainingRating { get; }
    /// <summary>Gets completed live training matches in insertion order.</summary>
    public IReadOnlyList<PlayerMatchRecord> History { get; }
    /// <summary>Gets the number of wins in this retained history.</summary>
    public int Wins { get; }
    /// <summary>Gets the number of draws in this retained history.</summary>
    public int Draws { get; }
    /// <summary>Gets the number of losses in this retained history.</summary>
    public int Losses { get; }
    /// <summary>Gets immutable quest projections.</summary>
    public IReadOnlyList<ChessQuestSnapshot> Quests { get; }
    /// <summary>Gets immutable stage unlock/completion projections.</summary>
    public IReadOnlyList<ChessStageSnapshot> Stages { get; }
    /// <summary>Gets local entitlements applied atomically with their quest claim identities.</summary>
    public IReadOnlyDictionary<ProgressionId, long> RewardBalances { get; }
    /// <summary>Gets typed ownership, equipment and purchasing availability.</summary>
    public IReadOnlyList<ChessCosmeticSnapshot> Cosmetics { get; }
    /// <summary>Gets the equipped board presentation ID.</summary>
    public string EquippedBoardId { get; }
    /// <summary>Gets the equipped piece-set presentation ID.</summary>
    public string EquippedPiecesId { get; }
    /// <summary>Gets the local starter training-point balance after purchases.</summary>
    public long TrainingPoints => RewardBalances.TryGetValue(ChessProgressIds.TrainingPoints, out long value) ? value : 0;
}

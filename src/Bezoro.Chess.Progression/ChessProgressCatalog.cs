using System.Security.Cryptography;
using System.Text;
using Bezoro.Chess.Study;
using Bezoro.Progression;

namespace Bezoro.Chess.Progression;

/// <summary>Stable starter counter, quest and local reward identities. They are content IDs, not player counters.</summary>
public static class ChessProgressIds
{
    /// <summary>The profile has been opened.</summary>
    public static ProgressionId ProfileOpened => new(1);
    /// <summary>A distinct live training match has finished.</summary>
    public static ProgressionId MatchesFinished => new(2);
    /// <summary>A distinct live training match was won.</summary>
    public static ProgressionId MatchesWon => new(3);
    /// <summary>A distinct curriculum lesson was completed.</summary>
    public static ProgressionId LessonsCompleted => new(4);
    /// <summary>The first local profile visit quest.</summary>
    public static ProgressionId FirstVisit => new(1001);
    /// <summary>The first lesson quest.</summary>
    public static ProgressionId FirstLesson => new(1002);
    /// <summary>The first training match quest.</summary>
    public static ProgressionId FirstMatch => new(1003);
    /// <summary>The first training win quest.</summary>
    public static ProgressionId FirstWin => new(1004);
    /// <summary>The five training matches quest.</summary>
    public static ProgressionId FiveMatches => new(1005);
    /// <summary>Local training points; no purchase, monetary value or online balance is implied.</summary>
    public static ProgressionId TrainingPoints => new(2001);
}

/// <summary>Immutable display content around an existing quest definition.</summary>
public sealed class ChessQuestDefinition
{
    /// <summary>Associates a readable title with a shared, immutable business definition.</summary>
    public ChessQuestDefinition(string title, QuestDefinition definition)
    {
        ProgressText.Require(title, nameof(title));
        Title = title;
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }
    /// <summary>Gets the default display title. A host may localize by quest identity.</summary>
    public string Title { get; }
    /// <summary>Gets the existing Framework objective/reward definition.</summary>
    public QuestDefinition Definition { get; }
}

/// <summary>Shares curriculum unlocks and quest content. Its fingerprint rejects incompatible save/catalog restores.</summary>
public sealed class ChessProgressCatalog
{
    private readonly ChessQuestDefinition[] _quests;
    /// <summary>Copies quest references; the campaign and underlying catalogs remain immutable shared content.</summary>
    public ChessProgressCatalog(ChessLearningCampaign campaign, IEnumerable<ChessQuestDefinition> quests, ChessCosmeticCatalog? cosmetics = null)
    {
        Campaign = campaign ?? throw new ArgumentNullException(nameof(campaign));
        Cosmetics = cosmetics ?? ChessCosmeticCatalog.CreateStarter();
        foreach (var stage in campaign.Stages) ProgressText.Require(stage.Lesson.Id, nameof(campaign));
        if (quests is null) throw new ArgumentNullException(nameof(quests));
        _quests = quests.ToArray();
        if (_quests.Any(quest => quest is null)) throw new ArgumentException("Quests cannot be null.", nameof(quests));
        Quests = Array.AsReadOnly(_quests);
        QuestCatalog = new QuestCatalog(_quests.Select(quest => quest.Definition).ToArray());
        foreach (var quest in _quests)
            foreach (var objective in quest.Definition.Objectives)
                if (objective.Counter != ChessProgressIds.ProfileOpened && objective.Counter != ChessProgressIds.MatchesFinished
                    && objective.Counter != ChessProgressIds.MatchesWon && objective.Counter != ChessProgressIds.LessonsCompleted)
                    throw new ArgumentException("A chess quest must observe a supported profile, match, win or lesson counter.", nameof(quests));
        Fingerprint = ComputeFingerprint();
    }
    /// <summary>Gets the immutable authored curriculum; its StoryCatalog is the only unlock authority.</summary>
    public ChessLearningCampaign Campaign { get; }
    /// <summary>Gets default quest titles and definitions.</summary>
    public IReadOnlyList<ChessQuestDefinition> Quests { get; }
    /// <summary>Gets the shared Framework quest catalog.</summary>
    public QuestCatalog QuestCatalog { get; }
    /// <summary>Gets typed local cosmetic offers and free defaults.</summary>
    public ChessCosmeticCatalog Cosmetics { get; }
    /// <summary>Gets the SHA-256 binding saved facts to the exact curriculum and quest definitions.</summary>
    public string Fingerprint { get; }

    /// <summary>Defines one-time profile, lesson, match, win and five-match quests as ordinary catalog data.</summary>
    public static ChessProgressCatalog CreateStarter(ChessLearningCampaign campaign) => new(campaign, new[]
    {
        Quest("Begin training", ChessProgressIds.FirstVisit, ChessProgressIds.ProfileOpened, 1, 10),
        Quest("Complete your first lesson", ChessProgressIds.FirstLesson, ChessProgressIds.LessonsCompleted, 1, 25),
        Quest("Finish a training match", ChessProgressIds.FirstMatch, ChessProgressIds.MatchesFinished, 1, 25),
        Quest("Win a training match", ChessProgressIds.FirstWin, ChessProgressIds.MatchesWon, 1, 50),
        Quest("Finish five training matches", ChessProgressIds.FiveMatches, ChessProgressIds.MatchesFinished, 5, 150)
    });

    private static ChessQuestDefinition Quest(string title, ProgressionId id, ProgressionId counter, long target, long reward) =>
        new(title, new QuestDefinition(id, new[] { new QuestObjective(counter, target) }, new[] { new RewardGrant(ChessProgressIds.TrainingPoints, reward) }));

    private string ComputeFingerprint()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Campaign.Id);
            writer.Write(Campaign.Stages.Count);
            foreach (var stage in Campaign.Stages)
            {
                writer.Write(stage.Id.Value); writer.Write(stage.Lesson.Id); writer.Write(stage.Lesson.Fingerprint);
                var definition = Campaign.Story.GetDefinition(stage.Id);
                writer.Write(definition.MaximumStars); writer.Write(definition.Prerequisites.Length);
                foreach (var prerequisite in definition.Prerequisites) { writer.Write(prerequisite.Stage.Value); writer.Write(prerequisite.RequiredStars); }
            }
            writer.Write(Cosmetics.Definitions.Count);
            foreach (var cosmetic in Cosmetics.Definitions)
            {
                writer.Write(cosmetic.Id); writer.Write((int)cosmetic.Slot); writer.Write(cosmetic.Price); writer.Write(cosmetic.IsDefault);
            }
            writer.Write(_quests.Length);
            foreach (var quest in _quests)
            {
                var definition = quest.Definition;
                writer.Write(definition.Id.Value); writer.Write(definition.Objectives.Length);
                foreach (var objective in definition.Objectives) { writer.Write(objective.Counter.Value); writer.Write(objective.Target); }
                writer.Write(definition.Rewards.Count);
                foreach (var reward in definition.Rewards.Rewards) { writer.Write(reward.Reward.Value); writer.Write(reward.Quantity); }
            }
        }
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
    }
}

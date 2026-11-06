using System.Globalization;
using Bezoro.Commerce;
using Bezoro.Chess.Study;
using Bezoro.Persistence;
using Bezoro.PlayerProfiles;
using Bezoro.Progression;

namespace Bezoro.Chess.Progression;

/// <summary>Immutable local rating and storage limits. Changing rating policy requires an explicit save migration.</summary>
public sealed class ChessProgressOptions
{
    /// <summary>Creates explicit limits. History never evicts its duplicate ledger; a full history rejects new matches.</summary>
    public ChessProgressOptions(double initialTrainingRating = 1200, double ratingKFactor = 32,
        int historyCapacity = 10000, int maximumSaveBytes = 4 * 1024 * 1024)
    {
        _ = new EloRating(initialTrainingRating);
        if (double.IsNaN(ratingKFactor) || double.IsInfinity(ratingKFactor) || ratingKFactor <= 0) throw new ArgumentOutOfRangeException(nameof(ratingKFactor));
        if (historyCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(historyCapacity));
        if (maximumSaveBytes < 256) throw new ArgumentOutOfRangeException(nameof(maximumSaveBytes));
        InitialTrainingRating = initialTrainingRating; RatingKFactor = ratingKFactor;
        HistoryCapacity = historyCapacity; MaximumSaveBytes = maximumSaveBytes;
    }
    /// <summary>Gets the initial local training rating.</summary>
    public double InitialTrainingRating { get; }
    /// <summary>Gets the training Elo K factor.</summary>
    public double RatingKFactor { get; }
    /// <summary>Gets the maximum retained matches, including their duplicate identities.</summary>
    public int HistoryCapacity { get; }
    /// <summary>Gets the maximum encoded application payload accepted when reading or writing.</summary>
    public int MaximumSaveBytes { get; }
}

/// <summary>Owns one local profile's progression commands and atomic persisted facts.</summary>
/// <remarks>
/// Use one application owner per save slot. Commands are serialized within this owner; other processes/owners require host coordination.
/// The host owns ISaveStore and its platform path. A successful atomic store write precedes snapshot publication; failures leave the visible snapshot unchanged.
/// Saved facts derive Elo, history, quests, story and reward balances through existing Framework modules. This is not an online authority or tamper-proof economy.
/// </remarks>
public sealed class ChessPlayerProgress
{
    /// <summary>The persistence schema for the bounded local application fact ledger.</summary>
    public const uint SaveSchemaVersion = 1;
    private readonly ISaveStore _store;
    private readonly string _slot;
    private readonly ChessProgressOptions _options;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private ProgressState _state;
    private ChessPlayerSnapshot _snapshot;

    private ChessPlayerProgress(ISaveStore store, string slot, ChessProgressCatalog catalog, ChessProgressOptions options, ProgressState state)
    {
        _store = store; _slot = slot; Catalog = catalog; _options = options; _state = state;
        _snapshot = state.Snapshot();
    }
    /// <summary>Gets immutable quest/curriculum content; it does not contain player counters.</summary>
    public ChessProgressCatalog Catalog { get; }
    /// <summary>Gets the last fully persisted immutable snapshot, safe to retain across subsequent commands.</summary>
    public ChessPlayerSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Restores a matching schema/catalog/player, or atomically creates a new local profile and first-visit quest progress.</summary>
    /// <remarks>Corrupt, incompatible or foreign saves throw and are never silently overwritten. Initial metadata is only used for a missing save.</remarks>
    public static async ValueTask<ChessPlayerProgress> OpenAsync(ISaveStore store, string slot, PlayerProfile initialProfile,
        ChessProgressCatalog catalog, ChessProgressOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (store is null) throw new ArgumentNullException(nameof(store));
        ProgressText.Require(slot, nameof(slot));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        ValidateProfile(initialProfile);
        options ??= new ChessProgressOptions();
        var buffer = new byte[Math.Min(4096, options.MaximumSaveBytes)];
        var read = await store.ReadAsync(slot, buffer, SaveSchemaVersion, cancellationToken).ConfigureAwait(false);
        if (read.Status == SaveReadStatus.DestinationTooSmall)
        {
            if (read.RequiredCapacity <= 0 || read.RequiredCapacity > options.MaximumSaveBytes)
                throw new InvalidDataException("The local profile exceeds the configured payload limit.");
            buffer = new byte[read.RequiredCapacity];
            read = await store.ReadAsync(slot, buffer, SaveSchemaVersion, cancellationToken).ConfigureAwait(false);
        }
        if (read.Status == SaveReadStatus.NotFound)
        {
            var initial = new ProgressState(initialProfile, catalog, options);
            var application = new ChessPlayerProgress(store, slot, catalog, options, initial);
            var payload = ProgressSaveCodec.Encode(initial);
            if (payload.Length > options.MaximumSaveBytes) throw new InvalidDataException("The profile exceeds the configured payload limit.");
            await store.WriteAsync(slot, SaveSchemaVersion, payload, cancellationToken).ConfigureAwait(false);
            return application;
        }
        if (!read.IsSuccess || read.BytesWritten <= 0 || read.BytesWritten > buffer.Length)
            throw new InvalidDataException("The local profile could not be restored: " + read.Status);
        var state = ProgressSaveCodec.Decode(buffer.AsMemory(0, read.BytesWritten), initialProfile.PlayerId, catalog, options);
        return new ChessPlayerProgress(store, slot, catalog, options, state);
    }

    /// <summary>Persists display-name/portrait metadata without changing the stable player identity.</summary>
    public ValueTask<bool> UpdateProfileAsync(string displayName, string? portraitId, CancellationToken cancellationToken = default) =>
        ChangeAsync(state =>
        {
            var profile = new PlayerProfile(state.Profile.PlayerId, displayName, portraitId);
            ValidateProfile(profile);
            if (state.Profile.DisplayName == profile.DisplayName && state.Profile.PortraitId == profile.PortraitId) return false;
            state.Profile = profile; return true;
        }, cancellationToken);

    /// <summary>Returns an authored lesson only when its StoryCatalog prerequisites are currently satisfied.</summary>
    public bool TryGetUnlockedLesson(ProgressionId stageId, out GuidedLessonDefinition? lesson)
    {
        var stage = Catalog.Campaign.GetStage(stageId);
        bool unlocked = Snapshot.Stages.Any(item => item.Id == stageId && item.IsUnlocked);
        lesson = unlocked ? stage.Lesson : null;
        return unlocked;
    }

    /// <summary>Records one trusted terminal live match. Identical retries return false; conflicting identities throw; replays award nothing.</summary>
    public ValueTask<bool> RecordMatchAsync(ChessTrainingMatch match, CancellationToken cancellationToken = default)
    {
        if (!match.IsValid) throw new ArgumentException("An initialized finalized match is required.", nameof(match));
        return ChangeAsync(state =>
        {
            if (match.Source == ChessMatchProgressSource.Replay) return false;
            if (state.Matches.Any(previous => previous.MatchId == match.MatchId))
            {
                if (state.Matches.First(previous => previous.MatchId == match.MatchId).Equals(match)) return false;
                throw new InvalidOperationException("A completed match identity already refers to different metadata.");
            }
            if (state.History.Count == _options.HistoryCapacity)
                throw new InvalidOperationException("The local history is full; migrate it without evicting duplicate identities before adding matches.");
            state.ApplyMatch(match); return true;
        }, cancellationToken);
    }

    /// <summary>Accepts only a Study-produced completion matching an unlocked campaign definition. Each lesson contributes progression once.</summary>
    /// <remarks>There is no public arbitrary-stage or arbitrary-stars completion command. Undo/replay cannot award the same lesson again.</remarks>
    public ValueTask<bool> CompleteLessonAsync(ChessLessonCompletion completion, CancellationToken cancellationToken = default)
    {
        if (completion is null) throw new ArgumentNullException(nameof(completion));
        return ChangeAsync(state =>
        {
            var stage = Catalog.Campaign.GetStage(completion.LessonId);
            if (stage.Lesson.Fingerprint != completion.DefinitionFingerprint)
                throw new ArgumentException("The completion belongs to a different authored lesson definition.", nameof(completion));
            if (state.Lessons.Any(previous => previous.LessonId == completion.LessonId)) return false;
            if (!state.Story.IsUnlocked(stage.Id)) return false;
            state.ApplyLesson(new LessonReceipt(completion.LessonId, completion.DefinitionFingerprint, completion.AttemptId, completion.MistakeCount));
            return true;
        }, cancellationToken);
    }

    /// <summary>Atomically persists a completed quest's claim identity and its local reward entitlements. Subsequent claims return false.</summary>
    public ValueTask<bool> ClaimQuestAsync(ProgressionId quest, CancellationToken cancellationToken = default) =>
        ChangeAsync(state => state.ApplyClaim(quest), cancellationToken);

    /// <summary>Purchases a permanent cosmetic once using local training points; insufficient funds and existing ownership return false.</summary>
    public ValueTask<bool> PurchaseCosmeticAsync(string cosmeticId, CancellationToken cancellationToken = default) =>
        ChangeAsync(state => state.ApplyPurchase(cosmeticId), cancellationToken);

    /// <summary>Persists an owned choice for its matching board or piece slot; wrong-slot, unowned and unchanged choices return false.</summary>
    public ValueTask<bool> EquipCosmeticAsync(ChessCosmeticSlot slot, string cosmeticId, CancellationToken cancellationToken = default) =>
        ChangeAsync(state => state.ApplyEquip(slot, cosmeticId), cancellationToken);

    private async ValueTask<bool> ChangeAsync(Func<ProgressState, bool> change, CancellationToken cancellationToken)
    {
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidate = _state.Clone();
            if (!change(candidate)) return false;
            var snapshot = candidate.Snapshot();
            byte[] payload = ProgressSaveCodec.Encode(candidate);
            if (payload.Length > _options.MaximumSaveBytes) throw new InvalidDataException("The profile exceeds the configured payload limit; no facts were evicted.");
            await _store.WriteAsync(_slot, SaveSchemaVersion, payload, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _state, candidate);
            Volatile.Write(ref _snapshot, snapshot);
            return true;
        }
        finally { _commands.Release(); }
    }

    internal bool HasRecordedMatch(string matchId) => Volatile.Read(ref _state).History.TryGet(matchId, out _);

    private static void ValidateProfile(PlayerProfile profile)
    {
        if (!profile.IsValid) throw new ArgumentException("An initialized player profile is required.", nameof(profile));
        ProgressText.Require(profile.PlayerId, nameof(profile)); ProgressText.Require(profile.DisplayName, nameof(profile));
        if (profile.PortraitId is not null) ProgressText.Require(profile.PortraitId, nameof(profile));
    }
}

internal readonly struct LessonReceipt(string lessonId, string fingerprint, Guid attemptId, int mistakeCount)
{
    internal string LessonId { get; } = lessonId;
    internal string Fingerprint { get; } = fingerprint;
    internal Guid AttemptId { get; } = attemptId;
    internal int MistakeCount { get; } = mistakeCount;
}

internal readonly struct CommerceFact
{
    internal CommerceFact(ProgressionId quest) { Quest = quest; CosmeticId = null; }
    internal CommerceFact(string cosmeticId) { Quest = default; CosmeticId = cosmeticId; }
    internal ProgressionId Quest { get; }
    internal string? CosmeticId { get; }
    internal bool IsClaim => CosmeticId is null;
}

internal sealed class ProgressState
{
    internal readonly ChessProgressCatalog Catalog;
    internal readonly ChessProgressOptions Options;
    internal readonly PlayerMatchHistory History;
    internal readonly QuestProgress Quests;
    internal readonly StoryProgress Story;
    internal readonly List<ChessTrainingMatch> Matches = new();
    internal readonly List<LessonReceipt> Lessons = new();
    internal readonly List<CommerceFact> CommerceFacts = new();
    internal readonly CommerceAccount Commerce;
    internal string EquippedBoardId;
    internal string EquippedPiecesId;
    internal PlayerProfile Profile;
    internal EloRating Rating;

    internal ProgressState(PlayerProfile profile, ChessProgressCatalog catalog, ChessProgressOptions options)
    {
        Profile = profile; Catalog = catalog; Options = options;
        Rating = new EloRating(options.InitialTrainingRating);
        History = new PlayerMatchHistory(profile.PlayerId, options.HistoryCapacity);
        Quests = new QuestProgress(catalog.QuestCatalog);
        Story = new StoryProgress(catalog.Campaign.Story);
        int capacity = checked(catalog.Cosmetics.Definitions.Count + catalog.Quests.Sum(quest => quest.Definition.Rewards.Count));
        Commerce = new CommerceAccount(catalog.Cosmetics.Store, profile.PlayerId, capacity);
        foreach (var cosmetic in catalog.Cosmetics.Definitions.Where(item => item.IsDefault))
            if (!Commerce.GrantSkin("default:" + cosmetic.Id, cosmetic.Id).Succeeded)
                throw new InvalidDataException("The initial free cosmetic could not be granted.");
        EquippedBoardId = catalog.Cosmetics.GetDefault(ChessCosmeticSlot.Board).Id;
        EquippedPiecesId = catalog.Cosmetics.GetDefault(ChessCosmeticSlot.Pieces).Id;
        foreach (var quest in catalog.Quests) Quests.TryActivate(quest.Definition.Id);
        Quests.Record(ChessProgressIds.ProfileOpened);
    }

    internal ProgressState Clone()
    {
        var clone = new ProgressState(Profile, Catalog, Options);
        foreach (var match in Matches) clone.ApplyMatch(match);
        foreach (var lesson in Lessons) clone.ApplyLesson(lesson);
        foreach (var fact in CommerceFacts)
            if (!(fact.IsClaim ? clone.ApplyClaim(fact.Quest) : clone.ApplyPurchase(fact.CosmeticId!)))
                throw new InvalidDataException("The saved commerce fact is not valid.");
        clone.RestoreEquipment(EquippedBoardId, EquippedPiecesId);
        return clone;
    }

    internal void ApplyMatch(ChessTrainingMatch match)
    {
        if (!match.IsValid || match.Source != ChessMatchProgressSource.LiveTraining) throw new InvalidDataException("Only initialized live training matches belong in progress history.");
        var record = new PlayerMatchRecord(match.MatchId, Profile.PlayerId, match.OpponentId, match.Outcome,
            match.CompletedAt, "local-training", true, match.ReplayReference);
        if (History.Append(record) != MatchHistoryAppendResult.Added) throw new InvalidDataException("Match identity is duplicated, conflicting, or history capacity was reached; no identities may be evicted.");
        Rating = EloRatings.ApplyResult(Rating, new EloRating(match.OpponentRating), match.Outcome, Options.RatingKFactor).FirstPlayer;
        Quests.Record(ChessProgressIds.MatchesFinished);
        if (match.Outcome == PlayerMatchOutcome.Win) Quests.Record(ChessProgressIds.MatchesWon);
        Matches.Add(match);
    }

    internal void ApplyLesson(LessonReceipt lesson)
    {
        ProgressText.Require(lesson.LessonId, nameof(lesson));
        var stage = Catalog.Campaign.GetStage(lesson.LessonId);
        if (lesson.AttemptId == Guid.Empty || lesson.MistakeCount < 0 || stage.Lesson.Fingerprint != lesson.Fingerprint
            || Lessons.Any(previous => previous.LessonId == lesson.LessonId || previous.AttemptId == lesson.AttemptId)
            || !Story.TryComplete(stage.Id, 1, out var completion) || !completion.IsFirstCompletion)
            throw new InvalidDataException("The lesson receipt is duplicated, locked, or belongs to another authored definition.");
        Lessons.Add(lesson);
        Quests.Record(ChessProgressIds.LessonsCompleted);
    }

    internal bool ApplyClaim(ProgressionId quest)
    {
        if (!Quests.TryClaim(quest, out var batch)) return false;
        var aggregated = new Dictionary<ProgressionId, long>();
        foreach (var reward in batch.Rewards)
        {
            aggregated.TryGetValue(reward.Reward, out long current);
            aggregated[reward.Reward] = checked(current + reward.Quantity);
        }
        foreach (var reward in aggregated.OrderBy(pair => pair.Key.Value))
        {
            string transaction = "quest:" + quest.Value.ToString(CultureInfo.InvariantCulture) + ":" + reward.Key.Value.ToString(CultureInfo.InvariantCulture);
            var result = Commerce.CreditCurrency(transaction, Currency(reward.Key), reward.Value);
            if (result.Status != CommerceTransactionStatus.Applied)
                throw new InvalidOperationException("The quest reward could not be applied atomically: " + result.Status);
        }
        CommerceFacts.Add(new CommerceFact(quest));
        return true;
    }

    internal bool ApplyPurchase(string id)
    {
        var definition = Catalog.Cosmetics.Get(id);
        if (definition.IsDefault || Commerce.OwnsSkin(id)) return false;
        var result = Commerce.Purchase("purchase:" + id, id, DateTimeOffset.MinValue);
        if (result.Status == CommerceTransactionStatus.InsufficientFunds) return false;
        if (result.Status != CommerceTransactionStatus.Applied)
            throw new InvalidOperationException("The local cosmetic purchase failed: " + result.Status);
        CommerceFacts.Add(new CommerceFact(id));
        return true;
    }

    internal bool ApplyEquip(ChessCosmeticSlot slot, string id)
    {
        if (slot is not (ChessCosmeticSlot.Board or ChessCosmeticSlot.Pieces)) throw new ArgumentOutOfRangeException(nameof(slot));
        var definition = Catalog.Cosmetics.Get(id);
        if (definition.Slot != slot || !Commerce.OwnsSkin(id)) return false;
        if (slot == ChessCosmeticSlot.Board)
        {
            if (EquippedBoardId == id) return false;
            EquippedBoardId = id;
        }
        else
        {
            if (EquippedPiecesId == id) return false;
            EquippedPiecesId = id;
        }
        return true;
    }

    internal void RestoreEquipment(string board, string pieces)
    {
        if (Catalog.Cosmetics.Get(board).Slot != ChessCosmeticSlot.Board || !Commerce.OwnsSkin(board)
            || Catalog.Cosmetics.Get(pieces).Slot != ChessCosmeticSlot.Pieces || !Commerce.OwnsSkin(pieces))
            throw new InvalidDataException("Saved equipment must belong to its slot and be owned.");
        EquippedBoardId = board; EquippedPiecesId = pieces;
    }

    private static string Currency(ProgressionId reward) => reward == ChessProgressIds.TrainingPoints
        ? ChessCosmeticCatalog.Currency : "reward-" + reward.Value.ToString(CultureInfo.InvariantCulture);

    internal Dictionary<ProgressionId, long> Balances()
    {
        var ids = new HashSet<ProgressionId> { ChessProgressIds.TrainingPoints };
        foreach (var quest in Catalog.Quests)
            foreach (var reward in quest.Definition.Rewards.Rewards) ids.Add(reward.Reward);
        return ids.OrderBy(id => id.Value).ToDictionary(id => id, id => Commerce.GetBalance(Currency(id)));
    }

    internal ChessPlayerSnapshot Snapshot()
    {
        var history = new PlayerMatchRecord[History.Count]; History.CopyTo(history);
        var quests = Catalog.Quests.Select(quest => new ChessQuestSnapshot(quest, Quests)).ToArray();
        var stages = Catalog.Campaign.Stages.Select(stage => new ChessStageSnapshot(stage.Id, stage.Lesson.Id, Story.IsUnlocked(stage.Id), Story.GetBestStars(stage.Id))).ToArray();
        long balance = Commerce.GetBalance(ChessCosmeticCatalog.Currency);
        var cosmetics = Catalog.Cosmetics.Definitions.Select(definition => new ChessCosmeticSnapshot(definition,
            Commerce.OwnsSkin(definition.Id), definition.Id == (definition.Slot == ChessCosmeticSlot.Board ? EquippedBoardId : EquippedPiecesId), balance)).ToArray();
        return new ChessPlayerSnapshot(Profile, Rating, history, quests, stages, Balances(), cosmetics, EquippedBoardId, EquippedPiecesId);
    }
}

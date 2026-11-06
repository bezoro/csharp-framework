using Bezoro.Chess.Progression;
using Bezoro.Chess.Study;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.Persistence;
using Bezoro.PlayerProfiles;
using Bezoro.Progression;
using Xunit;

namespace Bezoro.Chess.Progression.Tests;

public sealed class PlayerProgressTests
{
    [Fact]
    public async Task NewProfilePersistsMetadataFreeDefaultsAndFirstVisitWithoutGrantingUnclaimedRewards()
    {
        using var fixture = new Fixture();
        var app = await fixture.Open();
        Assert.Equal("Ada", app.Snapshot.Profile.DisplayName);
        Assert.Equal("portraits/knight", app.Snapshot.Profile.PortraitId);
        Assert.Equal(1200, app.Snapshot.TrainingRating.Value);
        Assert.Equal(0, app.Snapshot.TrainingRating.GamesPlayed);
        Assert.Equal(0, app.Snapshot.TrainingPoints);
        Assert.Equal(QuestStatus.Completed, Quest(app, ChessProgressIds.FirstVisit).Status);
        Assert.Equal("board.default", app.Snapshot.EquippedBoardId);
        Assert.Equal("pieces.default", app.Snapshot.EquippedPiecesId);
        Assert.All(app.Snapshot.Cosmetics.Where(item => item.IsDefault), item => { Assert.True(item.IsOwned); Assert.True(item.IsEquipped); Assert.False(item.CanPurchase); });
        Assert.All(app.Snapshot.Cosmetics.Where(item => !item.IsDefault), item => { Assert.False(item.IsOwned); Assert.False(item.CanPurchase); });
        Assert.True(File.Exists(fixture.SavePath));
    }

    [Fact]
    public async Task ProfileRenameAndPortraitRestoreWithoutOverwritingSavedMetadataFromStartupDefaults()
    {
        using var fixture = new Fixture();
        var app = await fixture.Open();
        Assert.True(await app.UpdateProfileAsync("João", "portraits/bishop"));
        int writes = fixture.Store.Writes;
        Assert.False(await app.UpdateProfileAsync("João", "portraits/bishop"));
        Assert.Equal(writes, fixture.Store.Writes);
        var restored = await fixture.Open();
        Assert.Equal("local-player", restored.Snapshot.Profile.PlayerId);
        Assert.Equal("João", restored.Snapshot.Profile.DisplayName);
        Assert.Equal("portraits/bishop", restored.Snapshot.Profile.PortraitId);
        Assert.True(await restored.UpdateProfileAsync("João", null));
        Assert.Null((await fixture.Open()).Snapshot.Profile.PortraitId);
    }

    [Fact]
    public async Task MatchResultsUseExistingEloAndRetainWinDrawLossHistoryAcrossReload()
    {
        using var fixture = new Fixture();
        var app = await fixture.Open();
        var rating = new EloRating(1200);
        foreach (var outcome in new[] { PlayerMatchOutcome.Win, PlayerMatchOutcome.Draw, PlayerMatchOutcome.Loss })
        {
            var match = Match("result-" + outcome, outcome);
            rating = EloRatings.ApplyResult(rating, new EloRating(match.OpponentRating), outcome).FirstPlayer;
            Assert.True(await app.RecordMatchAsync(match));
        }
        var restored = await fixture.Open();
        Assert.Equal(rating.Value, restored.Snapshot.TrainingRating.Value);
        Assert.Equal(3, restored.Snapshot.TrainingRating.GamesPlayed);
        Assert.Equal(1, restored.Snapshot.Wins); Assert.Equal(1, restored.Snapshot.Draws); Assert.Equal(1, restored.Snapshot.Losses);
        Assert.All(restored.Snapshot.History, match => Assert.Equal("local-training", match.GameMode));
    }

    [Fact]
    public async Task DuplicateMatchAndConflictingResultNeverFarmHistoryRatingOrQuests()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        var match = Match("same", PlayerMatchOutcome.Win);
        Assert.True(await app.RecordMatchAsync(match));
        int writes = fixture.Store.Writes;
        Assert.False(await app.RecordMatchAsync(match));
        Assert.Equal(writes, fixture.Store.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.RecordMatchAsync(Match("same", PlayerMatchOutcome.Loss)).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.RecordMatchAsync(Match("same", PlayerMatchOutcome.Win, rating: 1600)).AsTask());
        Assert.Equal(1, app.Snapshot.TrainingRating.GamesPlayed);
        Assert.Single(app.Snapshot.History);
        var restored = await fixture.Open();
        Assert.False(await restored.RecordMatchAsync(match));
        Assert.Equal(1, Quest(restored, ChessProgressIds.FiveMatches).Objectives[0].Current);
    }

    [Fact]
    public async Task ImportedReplayCannotAwardRatingHistoryOrQuestCounters()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        int writes = fixture.Store.Writes;
        Assert.False(await app.RecordMatchAsync(Match("replay", PlayerMatchOutcome.Win, source: ChessMatchProgressSource.Replay)));
        Assert.Equal(writes, fixture.Store.Writes);
        Assert.Empty(app.Snapshot.History); Assert.Equal(0, app.Snapshot.TrainingRating.GamesPlayed);
        Assert.Equal(0, Quest(app, ChessProgressIds.FiveMatches).Objectives[0].Current);
    }

    [Fact]
    public async Task ConcurrentDuplicateMatchesAndClaimsAreSerializedWithinTheOwner()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        bool[] matches = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => app.RecordMatchAsync(Match("one", PlayerMatchOutcome.Win)).AsTask()));
        Assert.Single(matches, value => value);
        bool[] claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => app.ClaimQuestAsync(ChessProgressIds.FirstWin).AsTask()));
        Assert.Single(claims, value => value);
        Assert.Equal(50, app.Snapshot.TrainingPoints);
        Assert.Single(app.Snapshot.History);
    }

    [Fact]
    public async Task QuestClaimAndRewardsCommitTogetherAndRestoreExactlyOnce()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        Assert.False(await app.ClaimQuestAsync(ChessProgressIds.FirstWin));
        Assert.True(await app.ClaimQuestAsync(ChessProgressIds.FirstVisit));
        Assert.False(await app.ClaimQuestAsync(ChessProgressIds.FirstVisit));
        var restored = await fixture.Open();
        Assert.Equal(10, restored.Snapshot.TrainingPoints);
        Assert.Equal(QuestStatus.Claimed, Quest(restored, ChessProgressIds.FirstVisit).Status);
        Assert.False(await restored.ClaimQuestAsync(ChessProgressIds.FirstVisit));
        Assert.Equal(10, restored.Snapshot.TrainingPoints);
    }

    [Fact]
    public async Task ClaimWriteFailureLeavesBothClaimAndWalletUnchangedAndRetryAppliesOnce()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        var before = app.Snapshot; fixture.Store.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => app.ClaimQuestAsync(ChessProgressIds.FirstVisit).AsTask());
        Assert.Same(before, app.Snapshot);
        Assert.Equal(0, (await fixture.Open()).Snapshot.TrainingPoints);
        fixture.Store.FailWrites = false;
        Assert.True(await app.ClaimQuestAsync(ChessProgressIds.FirstVisit));
        Assert.Equal(10, (await fixture.Open()).Snapshot.TrainingPoints);
    }

    [Fact]
    public async Task SnapshotIsPublishedOnlyAfterCommitAndCancellationPreservesThePreviousSave()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        var before = app.Snapshot;
        fixture.Store.BlockWrites = true;
        using var cancellation = new CancellationTokenSource();
        Task pending = app.RecordMatchAsync(Match("cancelled", PlayerMatchOutcome.Win), cancellation.Token).AsTask();
        await fixture.Store.Entered.Task;
        Assert.Same(before, app.Snapshot);
        Assert.Empty((await fixture.Open()).Snapshot.History);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Same(before, app.Snapshot);
        fixture.Store.BlockWrites = false;
        Assert.True(await app.RecordMatchAsync(Match("cancelled", PlayerMatchOutcome.Win)));
        Assert.Single(app.Snapshot.History);
    }

    [Fact]
    public async Task HistoryCapacityRejectsNewMatchesWithoutEvictingDuplicateProtection()
    {
        using var fixture = new Fixture(options: new ChessProgressOptions(historyCapacity: 1));
        var app = await fixture.Open();
        Assert.True(await app.RecordMatchAsync(Match("kept", PlayerMatchOutcome.Draw)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.RecordMatchAsync(Match("overflow", PlayerMatchOutcome.Win)).AsTask());
        Assert.False(await app.RecordMatchAsync(Match("kept", PlayerMatchOutcome.Draw)));
        Assert.Single(app.Snapshot.History);
        Assert.Single((await fixture.Open()).Snapshot.History);
    }

    [Fact]
    public async Task StudyProofAndStoryPrerequisitesOwnCompletionAndUnlocking()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        var first = fixture.Catalog.Campaign.Stages[0]; var second = fixture.Catalog.Campaign.Stages[1];
        Assert.True(app.TryGetUnlockedLesson(first.Id, out var initial)); Assert.Same(first.Lesson, initial);
        Assert.False(app.TryGetUnlockedLesson(second.Id, out var locked)); Assert.Null(locked);
        Assert.False(await app.CompleteLessonAsync(Complete(second.Lesson)));
        Assert.True(await app.CompleteLessonAsync(Complete(first.Lesson)));
        Assert.True(app.TryGetUnlockedLesson(second.Id, out var unlocked)); Assert.Same(second.Lesson, unlocked);
        Assert.True(await app.CompleteLessonAsync(Complete(second.Lesson)));
        var restored = await fixture.Open();
        Assert.All(restored.Snapshot.Stages, stage => Assert.Equal(1, stage.BestStars));
        Assert.Equal(QuestStatus.Completed, Quest(restored, ChessProgressIds.FirstLesson).Status);
        Assert.Empty(typeof(ChessLessonCompletion).GetConstructors());
    }

    [Fact]
    public async Task RepeatingOrResettingALessonCannotFarmCountersOrClaimedRewards()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        var lesson = fixture.Catalog.Campaign.Stages[0].Lesson;
        var receipt = Complete(lesson);
        Assert.True(await app.CompleteLessonAsync(receipt));
        Assert.True(await app.ClaimQuestAsync(ChessProgressIds.FirstLesson));
        Assert.False(await app.CompleteLessonAsync(receipt));
        Assert.False(await app.CompleteLessonAsync(Complete(lesson)));
        var restored = await fixture.Open();
        Assert.False(await restored.CompleteLessonAsync(Complete(lesson)));
        Assert.False(await restored.ClaimQuestAsync(ChessProgressIds.FirstLesson));
        Assert.Equal(25, restored.Snapshot.TrainingPoints);
    }

    [Fact]
    public async Task CompletionForAnotherDefinitionWithTheSameIdIsRejected()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        var other = Lesson("first", "d2d4");
        await Assert.ThrowsAsync<ArgumentException>(() => app.CompleteLessonAsync(Complete(other)).AsTask());
        Assert.Equal(0, app.Snapshot.Stages[0].BestStars);
    }

    [Fact]
    public async Task ObjectivePuzzleUsesTheSameCoreIssuedCompletionReceipt()
    {
        var lesson = new GuidedLessonDefinition("mate", "Mate", new PlayableMatchSetup(Fen.Parse("7k/8/5KQ1/8/8/8/8/8 w - - 0 1")!.Value),
            Array.Empty<GuidedLessonStep>(), mode: GuidedLessonMode.GoalPuzzle, puzzle: new ChessPuzzleDefinition(3));
        var catalog = ChessProgressCatalog.CreateStarter(new ChessLearningCampaign("puzzle", new[] { new ChessLearningStage(new(1), lesson) }));
        using var fixture = new Fixture(catalog); var app = await fixture.Open();
        Assert.True(await app.CompleteLessonAsync(Complete(lesson, "g6g7")));
        Assert.Equal(1, app.Snapshot.Stages[0].BestStars);
    }

    [Fact]
    public async Task PurchaseDeductsOnceAndEquipmentSlotsRemainIndependentAcrossReload()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        await EarnStarterRewards(app, fixture.Catalog);
        Assert.Equal(260, app.Snapshot.TrainingPoints);
        Assert.True(await app.PurchaseCosmeticAsync("board.midnight"));
        Assert.Equal(160, app.Snapshot.TrainingPoints);
        Assert.False(await app.PurchaseCosmeticAsync("board.midnight"));
        Assert.True(await app.EquipCosmeticAsync(ChessCosmeticSlot.Board, "board.midnight"));
        Assert.Equal("pieces.default", app.Snapshot.EquippedPiecesId);
        Assert.False(await app.EquipCosmeticAsync(ChessCosmeticSlot.Pieces, "board.midnight"));
        Assert.False(await app.EquipCosmeticAsync(ChessCosmeticSlot.Pieces, "pieces.ivory"));
        Assert.True(await app.PurchaseCosmeticAsync("pieces.ivory"));
        Assert.True(await app.EquipCosmeticAsync(ChessCosmeticSlot.Pieces, "pieces.ivory"));
        Assert.Equal(10, app.Snapshot.TrainingPoints);
        var restored = await fixture.Open();
        Assert.Equal(10, restored.Snapshot.TrainingPoints);
        Assert.Equal("board.midnight", restored.Snapshot.EquippedBoardId);
        Assert.Equal("pieces.ivory", restored.Snapshot.EquippedPiecesId);
        Assert.False(await restored.PurchaseCosmeticAsync("pieces.ivory"));
        Assert.False(await restored.EquipCosmeticAsync(ChessCosmeticSlot.Pieces, "pieces.ivory"));
    }

    [Fact]
    public async Task UnknownDefaultAndUnaffordablePurchasesNeverChangeTheWalletOrOwnership()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        var before = app.Snapshot; int writes = fixture.Store.Writes;
        Assert.False(await app.PurchaseCosmeticAsync("board.default"));
        Assert.False(await app.PurchaseCosmeticAsync("board.midnight"));
        await Assert.ThrowsAsync<ArgumentException>(() => app.PurchaseCosmeticAsync("missing").AsTask());
        Assert.Same(before, app.Snapshot); Assert.Equal(writes, fixture.Store.Writes);
        Assert.False(app.Snapshot.Cosmetics.Single(item => item.Id == "board.midnight").IsOwned);
    }

    [Fact]
    public async Task PurchaseAndEquipWriteFailuresDoNotPublishUncommittedEntitlementsOrSelections()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        await EarnStarterRewards(app, fixture.Catalog);
        var before = app.Snapshot; fixture.Store.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => app.PurchaseCosmeticAsync("board.midnight").AsTask());
        Assert.Same(before, app.Snapshot);
        fixture.Store.FailWrites = false;
        Assert.True(await app.PurchaseCosmeticAsync("board.midnight"));
        before = app.Snapshot; fixture.Store.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => app.EquipCosmeticAsync(ChessCosmeticSlot.Board, "board.midnight").AsTask());
        Assert.Same(before, app.Snapshot);
        Assert.Equal("board.default", (await fixture.Open()).Snapshot.EquippedBoardId);
    }

    [Fact]
    public async Task InterleavedClaimsAndPurchasesKeepTheSameDurableBalance()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        Assert.True(await app.ClaimQuestAsync(ChessProgressIds.FirstVisit));
        Assert.True(await app.RecordMatchAsync(Match("first-win", PlayerMatchOutcome.Win)));
        Assert.True(await app.ClaimQuestAsync(ChessProgressIds.FirstWin));
        Assert.True(await app.ClaimQuestAsync(ChessProgressIds.FirstMatch));
        Assert.True(await app.CompleteLessonAsync(Complete(fixture.Catalog.Campaign.Stages[0].Lesson)));
        Assert.True(await app.ClaimQuestAsync(ChessProgressIds.FirstLesson));
        Assert.True(await app.PurchaseCosmeticAsync("board.midnight"));
        Assert.Equal(10, app.Snapshot.TrainingPoints);
        for (int i = 0; i < 4; i++) Assert.True(await app.RecordMatchAsync(Match("more-" + i, PlayerMatchOutcome.Draw)));
        Assert.True(await app.ClaimQuestAsync(ChessProgressIds.FiveMatches));
        Assert.True(await app.PurchaseCosmeticAsync("pieces.ivory"));
        Assert.Equal(10, (await fixture.Open()).Snapshot.TrainingPoints);
    }

    [Fact]
    public async Task PurchaseCancellationKeepsOwnershipAndCurrencyTogether()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        await EarnStarterRewards(app, fixture.Catalog);
        var before = app.Snapshot; fixture.Store.BlockWrites = true;
        using var cancellation = new CancellationTokenSource();
        Task purchase = app.PurchaseCosmeticAsync("board.midnight", cancellation.Token).AsTask();
        await fixture.Store.Entered.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => purchase);
        Assert.Same(before, app.Snapshot);
        var restored = await fixture.Open();
        Assert.Equal(260, restored.Snapshot.TrainingPoints);
        Assert.False(restored.Snapshot.Cosmetics.Single(item => item.Id == "board.midnight").IsOwned);
    }

    [Theory]
    [InlineData("board.midnight")]
    [InlineData("pieces.default")]
    [InlineData("missing")]
    public async Task SavedEquipmentMustBeKnownOwnedAndInTheCorrectSlot(string replacement)
    {
        using var fixture = new Fixture(); await fixture.Open();
        byte[] payload = await fixture.Payload();
        byte[] original = System.Text.Encoding.UTF8.GetBytes("board.default");
        int at = payload.AsSpan().IndexOf(original);
        Assert.True(at >= 4);
        byte[] text = System.Text.Encoding.UTF8.GetBytes(replacement);
        var modified = payload.Take(at - 4).Concat(BitConverter.GetBytes(text.Length)).Concat(text).Concat(payload.Skip(at + original.Length)).ToArray();
        await fixture.Inner.WriteAsync("profile", ChessPlayerProgress.SaveSchemaVersion, modified);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Open().AsTask());
    }

    [Fact]
    public async Task AProfileLargerThanTheFirstReadBufferRestoresAllDuplicateIdentities()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        for (int index = 0; index < 20; index++)
            Assert.True(await app.RecordMatchAsync(Match(new string('x', 240) + index, PlayerMatchOutcome.Draw)));
        Assert.True((await fixture.Payload()).Length > 4096);
        var restored = await fixture.Open();
        Assert.Equal(20, restored.Snapshot.TrainingRating.GamesPlayed);
        Assert.False(await restored.RecordMatchAsync(Match(new string('x', 240) + 19, PlayerMatchOutcome.Draw)));
    }

    [Fact]
    public async Task RewardOverflowCannotClaimWithoutItsCurrencyCredit()
    {
        var quests = new[]
        {
            new ChessQuestDefinition("First", new QuestDefinition(ChessProgressIds.FirstVisit, new[] { new QuestObjective(ChessProgressIds.ProfileOpened, 1) }, new[] { new RewardGrant(ChessProgressIds.TrainingPoints, long.MaxValue) })),
            new ChessQuestDefinition("Next", new QuestDefinition(ChessProgressIds.FirstMatch, new[] { new QuestObjective(ChessProgressIds.MatchesFinished, 1) }, new[] { new RewardGrant(ChessProgressIds.TrainingPoints) }))
        };
        using var fixture = new Fixture(new ChessProgressCatalog(Campaign(), quests)); var app = await fixture.Open();
        Assert.True(await app.ClaimQuestAsync(ChessProgressIds.FirstVisit));
        Assert.True(await app.RecordMatchAsync(Match("one", PlayerMatchOutcome.Draw)));
        var before = app.Snapshot;
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.ClaimQuestAsync(ChessProgressIds.FirstMatch).AsTask());
        Assert.Same(before, app.Snapshot); Assert.Equal(long.MaxValue, app.Snapshot.TrainingPoints);
        Assert.Equal(QuestStatus.Completed, Quest(app, ChessProgressIds.FirstMatch).Status);
        Assert.Equal(long.MaxValue, (await fixture.Open()).Snapshot.TrainingPoints);
    }

    [Fact]
    public async Task HeldSnapshotsNeverMutateAfterLaterProfileClaimsOrPurchases()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        var before = app.Snapshot;
        await app.UpdateProfileAsync("New name", null);
        await app.ClaimQuestAsync(ChessProgressIds.FirstVisit);
        Assert.Equal("Ada", before.Profile.DisplayName); Assert.Equal(0, before.TrainingPoints);
        Assert.Equal(QuestStatus.Completed, before.Quests.Single(item => item.Id == ChessProgressIds.FirstVisit).Status);
        Assert.Throws<NotSupportedException>(() => ((IList<ChessQuestSnapshot>)before.Quests).Clear());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<ProgressionId, long>)before.RewardBalances).Clear());
    }

    [Fact]
    public async Task CorruptSchemaCatalogForeignPlayerAndTrailingDataNeverOverwriteAnExistingSave()
    {
        using var fixture = new Fixture(); await fixture.Open(); byte[] original = await File.ReadAllBytesAsync(fixture.SavePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => ChessPlayerProgress.OpenAsync(fixture.Store, "profile", new PlayerProfile("foreign", "Foreign"), fixture.Catalog).AsTask());
        var other = ChessProgressCatalog.CreateStarter(Campaign("changed"));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Open(other).AsTask());
        byte[] payload = await fixture.Payload();
        await fixture.Inner.WriteAsync("profile", 2, payload);
        byte[] wrongSchema = await File.ReadAllBytesAsync(fixture.SavePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Open().AsTask());
        Assert.Equal(wrongSchema, await File.ReadAllBytesAsync(fixture.SavePath));
        await File.WriteAllBytesAsync(fixture.SavePath, original);
        await fixture.Inner.WriteAsync("profile", ChessPlayerProgress.SaveSchemaVersion, payload.Concat(new byte[] { 42 }).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Open().AsTask());
        await File.WriteAllBytesAsync(fixture.SavePath, original.Select((value, index) => index == original.Length - 1 ? (byte)(value ^ 1) : value).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Open().AsTask());
    }

    [Fact]
    public async Task ClaimedWalletCannotBeChangedIndependentlyOfItsReceiptsEvenWithAValidEnvelope()
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        await app.ClaimQuestAsync(ChessProgressIds.FirstVisit);
        byte[] payload = await fixture.Payload();
        payload[^1] ^= 1;
        await fixture.Inner.WriteAsync("profile", ChessPlayerProgress.SaveSchemaVersion, payload);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Open().AsTask());
    }

    [Fact]
    public async Task ChangedRatingPolicyRequiresExplicitMigrationAndDoesNotResetTheProfile()
    {
        using var fixture = new Fixture(); await fixture.Open();
        await Assert.ThrowsAsync<InvalidDataException>(() => ChessPlayerProgress.OpenAsync(fixture.Store, "profile", fixture.Profile, fixture.Catalog,
            new ChessProgressOptions(initialTrainingRating: 1500)).AsTask());
        Assert.Equal(1200, (await fixture.Open()).Snapshot.TrainingRating.Value);
    }

    [Theory]
    [InlineData(PlayableMatchResultReason.Resignation)]
    [InlineData(PlayableMatchResultReason.Timeout)]
    public async Task RuleSessionDrawWhenTheOpponentCannotMateIsAccepted(PlayableMatchResultReason reason)
    {
        using var fixture = new Fixture(); var app = await fixture.Open();
        var match = new ChessTrainingMatch("draw-" + reason, "engine", 1200, 'w', new(reason, null), DateTimeOffset.UnixEpoch, ChessMatchProgressSource.LiveTraining);
        Assert.True(await app.RecordMatchAsync(match)); Assert.Equal(1, app.Snapshot.Draws);
    }

    [Fact]
    public void InvalidInputsAndCatalogsAreRejectedBeforeStorageOrBusinessMutation()
    {
        Assert.Throws<ArgumentException>(() => new ChessTrainingMatch("id", "engine", 1200, 'w', default, DateTimeOffset.UnixEpoch, ChessMatchProgressSource.LiveTraining));
        Assert.Throws<ArgumentException>(() => new ChessTrainingMatch("id", "engine", 1200, 'w', new(PlayableMatchResultReason.Stalemate, 'w'), DateTimeOffset.UnixEpoch, ChessMatchProgressSource.LiveTraining));
        Assert.Throws<ArgumentException>(() => new ChessTrainingMatch("id", "engine", 1200, 'w', new(PlayableMatchResultReason.Checkmate, null), DateTimeOffset.UnixEpoch, ChessMatchProgressSource.LiveTraining));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChessProgressOptions(ratingKFactor: double.NaN));
        Assert.Throws<ArgumentException>(() => new ChessCosmeticCatalog(Array.Empty<ChessCosmeticDefinition>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChessCosmeticDefinition("bad", "Bad", ChessCosmeticSlot.Board, 1, true));
        Assert.Throws<ArgumentException>(() => new ChessProgressCatalog(Campaign(), new[] { new ChessQuestDefinition("Unknown counter", new QuestDefinition(new(99), new[] { new QuestObjective(new(999), 1) })) }));
    }

    private static ChessQuestSnapshot Quest(ChessPlayerProgress app, ProgressionId id) => app.Snapshot.Quests.Single(quest => quest.Id == id);
    private static ChessTrainingMatch Match(string id, PlayerMatchOutcome outcome, double rating = 1200, ChessMatchProgressSource source = ChessMatchProgressSource.LiveTraining) =>
        new(id, "engine", rating, 'w', new(outcome == PlayerMatchOutcome.Draw ? PlayableMatchResultReason.DrawAgreement : PlayableMatchResultReason.Resignation,
            outcome == PlayerMatchOutcome.Draw ? null : outcome == PlayerMatchOutcome.Win ? 'w' : 'b'), DateTimeOffset.UnixEpoch, source);
    private static GuidedLessonDefinition Lesson(string id, string move = "e2e4") => new(id, id, PlayableMatchSetup.Standard,
        new[] { new GuidedLessonStep("Move the pawn", expectedPlayerMove: move, objective: GuidedLessonObjective.MovePiece) });
    private static ChessLearningCampaign Campaign(string id = "test") => new(id, new[]
    {
        new ChessLearningStage(new(1), Lesson("first")),
        new ChessLearningStage(new(2), Lesson("second", "d2d4"), new[] { new StoryPrerequisite(new(1)) })
    });
    private static ChessLessonCompletion Complete(GuidedLessonDefinition lesson, string? move = null)
    {
        var player = new GuidedLessonPlayer(); Assert.True(player.Start(lesson, out string error), error);
        string notation = move ?? lesson.Steps[0].ExpectedPlayerMove;
        Assert.True(player.EvaluateMoveRequest(notation).CanSubmitMove);
        var setup = new PlayableMatchSetup(lesson.InitialSetup!.Value.BaseFen, lesson.InitialSetup.Value.PlayedMoves.Add(notation));
        Assert.True(player.CompleteAcceptedMove(new GuidedLessonPosition(setup, setup.ResolveCurrentFen())).IsLessonComplete);
        return Assert.IsType<ChessLessonCompletion>(player.Completion);
    }
    private static async Task EarnStarterRewards(ChessPlayerProgress app, ChessProgressCatalog catalog)
    {
        await app.ClaimQuestAsync(ChessProgressIds.FirstVisit);
        await app.CompleteLessonAsync(Complete(catalog.Campaign.Stages[0].Lesson));
        await app.ClaimQuestAsync(ChessProgressIds.FirstLesson);
        for (int index = 0; index < 5; index++) await app.RecordMatchAsync(Match("earn-" + index, PlayerMatchOutcome.Win));
        await app.ClaimQuestAsync(ChessProgressIds.FirstMatch); await app.ClaimQuestAsync(ChessProgressIds.FirstWin);
        await app.ClaimQuestAsync(ChessProgressIds.FiveMatches);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "bezoro-chess-progress-" + Guid.NewGuid().ToString("N"));
        internal Fixture(ChessProgressCatalog? catalog = null, ChessProgressOptions? options = null)
        {
            Catalog = catalog ?? ChessProgressCatalog.CreateStarter(Campaign()); Options = options ?? new ChessProgressOptions();
            Inner = new AtomicFileSaveStore(_directory, Options.MaximumSaveBytes, false); Store = new ObservedStore(Inner);
        }
        internal PlayerProfile Profile => new("local-player", "Ada", "portraits/knight");
        internal string SavePath => Path.Combine(_directory, "profile.bzsave");
        internal ChessProgressCatalog Catalog { get; }
        internal ChessProgressOptions Options { get; }
        internal AtomicFileSaveStore Inner { get; }
        internal ObservedStore Store { get; }
        internal ValueTask<ChessPlayerProgress> Open(ChessProgressCatalog? catalog = null) => ChessPlayerProgress.OpenAsync(Store, "profile", Profile, catalog ?? Catalog, Options);
        internal async Task<byte[]> Payload()
        {
            var buffer = new byte[Options.MaximumSaveBytes]; var read = await Inner.ReadAsync("profile", buffer);
            Assert.True(read.IsSuccess); return buffer.AsSpan(0, read.BytesWritten).ToArray();
        }
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }

    private sealed class ObservedStore(ISaveStore inner) : ISaveStore
    {
        internal int Writes;
        internal bool FailWrites;
        internal bool BlockWrites;
        internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask WriteAsync(string slot, uint schemaVersion, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Writes);
            if (FailWrites) throw new IOException("Injected atomic precommit failure.");
            if (BlockWrites)
            {
                Entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            await inner.WriteAsync(slot, schemaVersion, payload, cancellationToken);
        }
        public ValueTask<SaveReadResult> ReadAsync(string slot, Memory<byte> destination, uint? expectedSchemaVersion = null, CancellationToken cancellationToken = default) => inner.ReadAsync(slot, destination, expectedSchemaVersion, cancellationToken);
        public ValueTask DeleteAsync(string slot, CancellationToken cancellationToken = default) => inner.DeleteAsync(slot, cancellationToken);
    }
}

using Bezoro.Progression;
using Xunit;

namespace Bezoro.Progression.Tests;

public sealed class QuestProgressTests
{
    private static readonly ProgressionId Wins = new(1);
    private static readonly ProgressionId Lessons = new(2);
    private static readonly ProgressionId FirstQuest = new(10);
    private static readonly ProgressionId SecondQuest = new(11);
    private static readonly ProgressionId Coins = new(100);

    [Fact]
    public void DefinitionsCopyInputsAndRejectDuplicateOrInvalidContent()
    {
        var objectives = new[] { new QuestObjective(Wins, 3) };
        var rewards = new[] { new RewardGrant(Coins, 100) };
        var quest = new QuestDefinition(FirstQuest, objectives, rewards);
        objectives[0] = new QuestObjective(Lessons, 99);
        rewards[0] = new RewardGrant(Coins, 1);
        Assert.Equal(Wins, quest.Objectives[0].Counter);
        Assert.Equal(100, quest.Rewards[0].Quantity);
        Assert.Throws<ArgumentException>(() => new QuestDefinition(default, objectives));
        Assert.Throws<ArgumentException>(() => new QuestDefinition(FirstQuest, Array.Empty<QuestObjective>()));
        Assert.Throws<ArgumentException>(() => new QuestDefinition(FirstQuest, new QuestObjective[] { default }));
        Assert.Throws<ArgumentException>(() => new QuestDefinition(FirstQuest, new[] { objectives[0], objectives[0] }));
        Assert.Throws<ArgumentException>(() => new QuestDefinition(FirstQuest, objectives, new RewardGrant[] { default }));
        Assert.Throws<ArgumentException>(() => new QuestCatalog(new[] { quest, quest }));
        Assert.Throws<ArgumentException>(() => new QuestCatalog(new QuestDefinition[] { null! }));
    }

    [Fact]
    public void OnlyActiveMatchingQuestsReceiveCountersAndCompletionRequiresEveryObjective()
    {
        QuestProgress progress = CreateProgress();
        Assert.Equal(0, progress.Record(Wins, 100));
        Assert.Equal(0, progress.GetObjectiveProgress(FirstQuest, 0));
        Assert.True(progress.TryActivate(FirstQuest));
        Assert.False(progress.TryActivate(FirstQuest));
        Assert.False(progress.TryClaim(FirstQuest, out _));
        Assert.Equal(0, progress.Record(new ProgressionId(999)));
        Assert.Equal(0, progress.Record(Wins, 2));
        Assert.Equal(QuestStatus.Active, progress.GetStatus(FirstQuest));
        Assert.Equal(1, progress.Record(Lessons));
        Assert.Equal(QuestStatus.Completed, progress.GetStatus(FirstQuest));
        Assert.Equal(QuestStatus.Inactive, progress.GetStatus(SecondQuest));
        Assert.Equal(0, progress.Record(Wins, long.MaxValue));
        Assert.Equal(2, progress.GetObjectiveProgress(FirstQuest, 0));
        Assert.True(progress.TryClaim(FirstQuest, out RewardBatch rewards));
        Assert.Equal(new RewardGrant(Coins, 20), rewards[0]);
        Assert.Equal(QuestStatus.Claimed, progress.GetStatus(FirstQuest));
        Assert.False(progress.TryClaim(FirstQuest, out rewards));
        Assert.Equal(0, rewards.Count);
    }

    [Fact]
    public void SharedCounterCanCompleteSeveralQuestsAndSaturatesWithoutOverflow()
    {
        var catalog = new QuestCatalog(new[]
        {
            new QuestDefinition(FirstQuest, new[] { new QuestObjective(Wins, long.MaxValue) }),
            new QuestDefinition(SecondQuest, new[] { new QuestObjective(Wins, 2) })
        });
        var progress = new QuestProgress(catalog);
        progress.TryActivate(FirstQuest);
        progress.TryActivate(SecondQuest);
        Assert.Equal(0, progress.Record(Wins));
        Assert.Equal(2, progress.Record(Wins, long.MaxValue));
        Assert.Equal(long.MaxValue, progress.GetObjectiveProgress(FirstQuest, 0));
        Assert.Equal(2, progress.GetObjectiveProgress(SecondQuest, 0));
        Assert.Equal(0, progress.Record(Wins));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void InvalidEventAmountDoesNotChangeState(long amount)
    {
        QuestProgress progress = CreateProgress();
        progress.TryActivate(FirstQuest);
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.Record(Wins, amount));
        Assert.Equal(0, progress.GetObjectiveProgress(FirstQuest, 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void InvalidObjectiveIndexIsRejected(int objectiveIndex)
    {
        QuestProgress progress = CreateProgress();
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.GetObjectiveProgress(FirstQuest, objectiveIndex));
    }

    [Fact]
    public void UnknownIdentifiersAreRejectedBeforeMutation()
    {
        QuestProgress progress = CreateProgress();
        Assert.Throws<ArgumentException>(() => progress.GetStatus(default));
        Assert.Throws<ArgumentException>(() => progress.Record(default));
        Assert.Throws<ArgumentException>(() => progress.TryActivate(new ProgressionId(99)));
        Assert.Throws<ArgumentException>(() => progress.TryClaim(new ProgressionId(99), out _));
        Assert.Throws<ArgumentNullException>(() => new QuestProgress(null!));
    }

    [Fact]
    public void RestoreRoundTripsEveryLifecycleAndRejectsInvalidStateAtomically()
    {
        QuestProgress progress = CreateProgress();
        progress.TryActivate(FirstQuest);
        progress.Record(Wins);
        var statuses = new QuestStatus[2];
        var counters = new long[3];
        progress.CopyStateTo(statuses, counters);
        var restored = new QuestProgress(progress.Catalog);
        Assert.True(restored.TryRestore(statuses, counters));
        Assert.Equal(1, restored.GetObjectiveProgress(FirstQuest, 0));
        Assert.Equal(QuestStatus.Active, restored.GetStatus(FirstQuest));
        Assert.False(restored.TryRestore(new[] { QuestStatus.Active }, counters));
        Assert.False(restored.TryRestore(statuses, new long[2]));
        Assert.False(restored.TryRestore(new[] { (QuestStatus)99, QuestStatus.Inactive }, counters));
        Assert.False(restored.TryRestore(new[] { QuestStatus.Inactive, QuestStatus.Inactive }, counters));
        Assert.False(restored.TryRestore(new[] { QuestStatus.Completed, QuestStatus.Inactive }, counters));
        Assert.False(restored.TryRestore(statuses, new long[] { 3, 0, 0 }));
        Assert.False(restored.TryRestore(statuses, new long[] { -1, 0, 0 }));
        Assert.False(restored.TryRestore(statuses, new long[] { 2, 1, 0 }));
        Assert.Equal(1, restored.GetObjectiveProgress(FirstQuest, 0));
        Assert.Equal(QuestStatus.Active, restored.GetStatus(FirstQuest));
        Assert.True(restored.TryRestore(new[] { QuestStatus.Completed, QuestStatus.Inactive }, new long[] { 2, 1, 0 }));
        Assert.True(restored.TryClaim(FirstQuest, out _));
        restored.CopyStateTo(statuses, counters);
        Assert.Equal(QuestStatus.Claimed, statuses[0]);
        Assert.True(progress.TryRestore(statuses, counters));
        Assert.False(progress.TryClaim(FirstQuest, out _));
    }

    [Fact]
    public void CopyRejectsEitherShortDestinationBeforeWritingAnything()
    {
        QuestProgress progress = CreateProgress();
        progress.TryActivate(FirstQuest);
        var statuses = new[] { QuestStatus.Claimed, QuestStatus.Claimed };
        Assert.Throws<ArgumentException>(() => progress.CopyStateTo(statuses, new long[1]));
        Assert.All(statuses, status => Assert.Equal(QuestStatus.Claimed, status));
        Assert.Throws<ArgumentException>(() => progress.CopyStateTo(new QuestStatus[1], new long[3]));
    }

    [Fact]
    public void EmptyCatalogAndDefaultRewardBatchHaveEmptyState()
    {
        var progress = new QuestProgress(new QuestCatalog(Array.Empty<QuestDefinition>()));
        Assert.Equal(0, progress.Catalog.Count);
        Assert.Equal(0, progress.Catalog.ObjectiveCount);
        Assert.True(progress.TryRestore(Array.Empty<QuestStatus>(), Array.Empty<long>()));
        progress.CopyStateTo(Span<QuestStatus>.Empty, Span<long>.Empty);
        Assert.Equal(0, default(RewardBatch).Count);
        Assert.True(default(RewardBatch).Rewards.IsEmpty);
    }

    [Fact]
    public void SeededEventsMatchIndependentCounterModel()
    {
        var random = new Random(12937);
        var definitions = new QuestDefinition[40];
        var model = new long[40, 3];
        var targets = new long[40, 3];
        var active = new bool[40];
        var claimed = new bool[40];
        for (int quest = 0; quest < definitions.Length; quest++)
        {
            var objectives = new QuestObjective[3];
            for (int objective = 0; objective < 3; objective++)
            {
                targets[quest, objective] = random.Next(1, 500);
                objectives[objective] = new QuestObjective(new ProgressionId((uint)(objective + 1)), targets[quest, objective]);
            }
            definitions[quest] = new QuestDefinition(new ProgressionId((uint)(quest + 100)), objectives);
        }
        var progress = new QuestProgress(new QuestCatalog(definitions));
        for (int iteration = 0; iteration < 5000; iteration++)
        {
            int selectedQuest = random.Next(40);
            if (!active[selectedQuest])
            {
                active[selectedQuest] = true;
                Assert.True(progress.TryActivate(definitions[selectedQuest].Id));
            }
            int selectedCounter = random.Next(3);
            long amount = random.Next(1, 10);
            int newlyCompleted = 0;
            for (int quest = 0; quest < definitions.Length; quest++)
            {
                if (!active[quest]) continue;
                bool wasCompleted = Completed(quest);
                model[quest, selectedCounter] = Math.Min(targets[quest, selectedCounter], model[quest, selectedCounter] + amount);
                if (!wasCompleted && Completed(quest)) newlyCompleted++;
            }
            Assert.Equal(newlyCompleted, progress.Record(new ProgressionId((uint)(selectedCounter + 1)), amount));
            bool shouldClaim = Completed(selectedQuest) && !claimed[selectedQuest];
            Assert.Equal(shouldClaim, progress.TryClaim(definitions[selectedQuest].Id, out _));
            claimed[selectedQuest] |= shouldClaim;
            for (int objective = 0; objective < 3; objective++)
                Assert.Equal(model[selectedQuest, objective], progress.GetObjectiveProgress(definitions[selectedQuest].Id, objective));
            QuestStatus expected = !active[selectedQuest] ? QuestStatus.Inactive : claimed[selectedQuest] ? QuestStatus.Claimed : Completed(selectedQuest) ? QuestStatus.Completed : QuestStatus.Active;
            Assert.Equal(expected, progress.GetStatus(definitions[selectedQuest].Id));
        }
        bool Completed(int quest) => model[quest, 0] == targets[quest, 0] && model[quest, 1] == targets[quest, 1] && model[quest, 2] == targets[quest, 2];
    }

    [Fact]
    public void WarmedEventAndClaimOperationsDoNotAllocate()
    {
        var catalog = new QuestCatalog(new[] { new QuestDefinition(FirstQuest, new[] { new QuestObjective(Wins, long.MaxValue) }, new[] { new RewardGrant(Coins) }) });
        var progress = new QuestProgress(catalog);
        progress.TryActivate(FirstQuest);
        var completed = new[] { QuestStatus.Completed };
        var target = new[] { long.MaxValue };
        for (int index = 0; index < 100; index++) progress.Record(Wins);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++) progress.Record(Wins);
        long eventAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        progress.TryRestore(completed, target);
        progress.TryClaim(FirstQuest, out _);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++)
        {
            progress.TryRestore(completed, target);
            progress.TryClaim(FirstQuest, out _);
        }
        long claimAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, eventAllocated);
        Assert.Equal(0, claimAllocated);
    }

    private static QuestProgress CreateProgress() => new(new QuestCatalog(new[]
    {
        new QuestDefinition(FirstQuest, new[] { new QuestObjective(Wins, 2), new QuestObjective(Lessons, 1) }, new[] { new RewardGrant(Coins, 20) }),
        new QuestDefinition(SecondQuest, new[] { new QuestObjective(Wins, 3) })
    }));
}

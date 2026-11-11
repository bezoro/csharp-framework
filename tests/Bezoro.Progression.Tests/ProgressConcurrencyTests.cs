using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Bezoro.Progression.Tests;

public sealed class ProgressConcurrencyTests
{
    [Fact]
    public void ConcurrentQuestEventsRetainCountsAndAwardOneClaim()
    {
        var quest = new ProgressionId(1);
        var counter = new ProgressionId(2);
        var progress = new QuestProgress(new QuestCatalog(new[]
        {
            new QuestDefinition(quest, new[] { new QuestObjective(counter, 8_000) }, new[] { new RewardGrant(new ProgressionId(3)) })
        }));
        Assert.True(progress.TryActivate(quest));
        int completed = 0, claimed = 0;
        Parallel.For(0, 4, _ =>
        {
            for (int index = 0; index < 2_000; index++) Interlocked.Add(ref completed, progress.Record(counter));
        });
        Parallel.For(0, 1_000, worker => { if (progress.TryClaim(quest, out _)) Interlocked.Increment(ref claimed); });
        Assert.Equal(1, completed);
        Assert.Equal(1, claimed);
        Assert.Equal(8_000, progress.GetObjectiveProgress(quest, 0));
        Assert.Equal(QuestStatus.Claimed, progress.GetStatus(quest));
    }

    [Fact]
    public void ConcurrentExperienceAndTrackClaimsCannotLoseUpdatesOrDuplicateRewards()
    {
        var reward = new RewardGrant(new ProgressionId(1));
        var progress = new BattlePassProgress(new BattlePassDefinition(new[] { new BattlePassTierDefinition(1, new[] { reward }, new[] { reward }) }));
        Parallel.For(0, 4, _ => { for (int index = 0; index < 2_000; index++) progress.AddExperience(1); });
        int premiumGrants = 0, claims = 0;
        Parallel.For(0, 1_000, index =>
        {
            if (progress.GrantPremiumAccess()) Interlocked.Increment(ref premiumGrants);
            if (progress.TryClaim(0, (index & 1) == 0 ? BattlePassTrack.Free : BattlePassTrack.Premium, out _)) Interlocked.Increment(ref claims);
        });
        Assert.Equal(8_000, progress.Experience);
        Assert.Equal(1, premiumGrants);
        Assert.Equal(2, claims);
    }

    [Fact]
    public void BattlePassCopiesExperienceAccessAndClaimsFromOneRestore()
    {
        var reward = new RewardGrant(new ProgressionId(1));
        var progress = new BattlePassProgress(new BattlePassDefinition(new[] { new BattlePassTierDefinition(1, new[] { reward }, new[] { reward }) }));
        Parallel.Invoke(() =>
        {
            for (int index = 0; index < 2_000; index++)
            {
                bool earned = (index & 1) != 0;
                Assert.True(progress.TryRestore(earned ? 1 : 0, earned, new[] { earned ? (byte)3 : (byte)0 }));
            }
        }, () =>
        {
            Span<byte> claims = stackalloc byte[1];
            for (int index = 0; index < 2_000; index++)
            {
                progress.CopyStateTo(claims, out long experience, out bool premium);
                Assert.Equal(experience == 1, premium);
                Assert.Equal(experience == 1 ? 3 : 0, claims[0]);
            }
        });
        Assert.Throws<ArgumentException>(() => progress.CopyStateTo(Span<byte>.Empty, out _, out _));
    }

    [Fact]
    public void ConcurrentStoryCompletionsHaveOneFirstCompletionAndKeepTheBestScore()
    {
        var stage = new ProgressionId(1);
        var progress = new StoryProgress(new StoryCatalog(new[] { new StoryStageDefinition(stage) }));
        int first = 0;
        Parallel.For(0, 2_000, index =>
        {
            if (progress.TryComplete(stage, (byte)(index % 3 + 1), out var result) && result.IsFirstCompletion)
                Interlocked.Increment(ref first);
        });
        Assert.Equal(1, first);
        Assert.Equal(3, progress.GetBestStars(stage));
    }
}

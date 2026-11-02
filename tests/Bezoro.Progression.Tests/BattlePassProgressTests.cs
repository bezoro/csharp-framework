using Bezoro.Progression;
using Xunit;

namespace Bezoro.Progression.Tests;

public sealed class BattlePassProgressTests
{
    private static readonly RewardGrant FreeReward = new(new ProgressionId(1), 50);
    private static readonly RewardGrant PremiumReward = new(new ProgressionId(2));

    [Fact]
    public void DefinitionCopiesTiersAndRewardArrays()
    {
        var rewards = new[] { FreeReward };
        var firstTier = new BattlePassTierDefinition(100, rewards);
        rewards[0] = PremiumReward;
        var tiers = new[] { firstTier };
        var definition = new BattlePassDefinition(tiers);
        tiers[0] = new BattlePassTierDefinition(200, rewards);
        Assert.Equal(100, definition.Tiers[0].RequiredExperience);
        Assert.Equal(FreeReward, definition.Tiers[0].FreeRewards[0]);
    }

    [Fact]
    public void FreeAndPremiumClaimsAreIndependentAndRetroactive()
    {
        BattlePassProgress progress = CreateProgress();
        Assert.False(progress.TryClaim(0, BattlePassTrack.Free, out _));
        Assert.Equal(100, progress.AddExperience(100));
        Assert.Equal(1, progress.UnlockedTierCount);
        Assert.True(progress.TryClaim(0, BattlePassTrack.Free, out RewardBatch free));
        Assert.Equal(FreeReward, free[0]);
        Assert.False(progress.TryClaim(0, BattlePassTrack.Free, out _));
        Assert.False(progress.TryClaim(0, BattlePassTrack.Premium, out _));
        Assert.True(progress.GrantPremiumAccess());
        Assert.False(progress.GrantPremiumAccess());
        Assert.True(progress.TryClaim(0, BattlePassTrack.Premium, out RewardBatch premium));
        Assert.Equal(PremiumReward, premium[0]);
        Assert.True(progress.IsClaimed(0, BattlePassTrack.Free));
        Assert.True(progress.IsClaimed(0, BattlePassTrack.Premium));
        Assert.False(progress.TryClaim(1, BattlePassTrack.Free, out _));
        progress.AddExperience(200);
        Assert.False(progress.TryClaim(1, BattlePassTrack.Free, out _));
        Assert.True(progress.TryClaim(1, BattlePassTrack.Premium, out _));
        Assert.Equal(2, progress.UnlockedTierCount);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(99, 0)]
    [InlineData(100, 1)]
    [InlineData(299, 1)]
    [InlineData(300, 2)]
    [InlineData(long.MaxValue, 2)]
    public void ThresholdBoundariesUseCumulativeExperience(long experience, int unlocked)
    {
        Assert.Equal(unlocked, CreateProgress().Definition.GetUnlockedTierCount(experience));
    }

    [Fact]
    public void ExperienceSaturatesAndReportsOnlyCreditedAmount()
    {
        BattlePassProgress progress = CreateProgress();
        Assert.Equal(1, progress.AddExperience(1));
        Assert.Equal(long.MaxValue - 1, progress.AddExperience(long.MaxValue));
        Assert.Equal(long.MaxValue, progress.Experience);
        Assert.Equal(0, progress.AddExperience(1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void NonpositiveExperienceIsRejected(long amount)
    {
        var progress = CreateProgress();
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.AddExperience(amount));
        Assert.Equal(0, progress.Experience);
    }

    [Fact]
    public void InvalidDefinitionsAndArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new BattlePassProgress(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BattlePassTierDefinition(0, new[] { FreeReward }));
        Assert.Throws<ArgumentException>(() => new BattlePassTierDefinition(100));
        Assert.Throws<ArgumentException>(() => new BattlePassTierDefinition(100, new RewardGrant[] { default }));
        var tier = new BattlePassTierDefinition(100, new[] { FreeReward });
        Assert.Throws<ArgumentException>(() => new BattlePassDefinition(new[] { tier, tier }));
        Assert.Throws<ArgumentException>(() => new BattlePassDefinition(new BattlePassTierDefinition[] { null! }));
        var progress = CreateProgress();
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.TryClaim(-1, BattlePassTrack.Free, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.IsClaimed(2, BattlePassTrack.Free));
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.TryClaim(0, (BattlePassTrack)255, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.Definition.GetUnlockedTierCount(-1));
    }

    [Fact]
    public void RestoreRejectsImpossibleClaimsAndLeavesExistingStateIntact()
    {
        BattlePassProgress progress = CreateProgress();
        progress.AddExperience(100);
        progress.TryClaim(0, BattlePassTrack.Free, out _);
        Assert.False(progress.TryRestore(-1, false, new byte[] { 0, 0 }));
        Assert.False(progress.TryRestore(100, false, new byte[] { 0 }));
        Assert.False(progress.TryRestore(100, false, new byte[] { 4, 0 }));
        Assert.False(progress.TryRestore(99, false, new byte[] { 1, 0 }));
        Assert.False(progress.TryRestore(300, false, new byte[] { 2, 0 }));
        Assert.False(progress.TryRestore(300, true, new byte[] { 0, 1 }));
        Assert.Equal(100, progress.Experience);
        Assert.False(progress.HasPremiumAccess);
        Assert.True(progress.IsClaimed(0, BattlePassTrack.Free));
        Assert.True(progress.TryRestore(300, true, new byte[] { 3, 2 }));
        Assert.True(progress.HasPremiumAccess);
        Assert.Equal(300, progress.Experience);
        Assert.True(progress.IsClaimed(1, BattlePassTrack.Premium));
        var copy = new byte[2];
        progress.CopyClaimStateTo(copy);
        Assert.Equal(new byte[] { 3, 2 }, copy);
        Assert.Throws<ArgumentException>(() => progress.CopyClaimStateTo(new byte[1]));
    }

    [Fact]
    public void EmptyPassHasNoTiersAndCanRoundTripState()
    {
        var progress = new BattlePassProgress(new BattlePassDefinition(Array.Empty<BattlePassTierDefinition>()));
        Assert.Equal(0, progress.UnlockedTierCount);
        Assert.True(progress.TryRestore(10, true, Array.Empty<byte>()));
        progress.CopyClaimStateTo(Span<byte>.Empty);
        Assert.Equal(10, progress.Experience);
    }

    [Fact]
    public void SeededClaimsMatchIndependentSetModel()
    {
        var tiers = Enumerable.Range(1, 50).Select(index => new BattlePassTierDefinition(index * 100L, new[] { FreeReward }, new[] { PremiumReward })).ToArray();
        var progress = new BattlePassProgress(new BattlePassDefinition(tiers));
        var claimed = new HashSet<(int Tier, BattlePassTrack Track)>();
        var random = new Random(951);
        long expectedExperience = 0;
        bool premium = false;
        for (int iteration = 0; iteration < 5000; iteration++)
        {
            if (random.Next(4) == 0)
            {
                long amount = random.Next(1, 20);
                expectedExperience += amount;
                progress.AddExperience(amount);
            }
            if (iteration == 300)
            {
                premium = true;
                progress.GrantPremiumAccess();
            }
            int tier = random.Next(tiers.Length);
            BattlePassTrack track = random.Next(2) == 0 ? BattlePassTrack.Free : BattlePassTrack.Premium;
            bool eligible = expectedExperience >= (tier + 1) * 100L && (track == BattlePassTrack.Free || premium);
            bool expected = eligible && claimed.Add((tier, track));
            Assert.Equal(expected, progress.TryClaim(tier, track, out RewardBatch rewards));
            Assert.Equal(expected ? 1 : 0, rewards.Count);
            Assert.Equal(claimed.Contains((tier, track)), progress.IsClaimed(tier, track));
        }
    }

    [Fact]
    public void WarmedProgressQueriesAndClaimsDoNotAllocate()
    {
        var progress = CreateProgress();
        var claims = new byte[] { 0, 0 };
        progress.TryRestore(100, true, claims);
        progress.TryClaim(0, BattlePassTrack.Free, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++)
        {
            progress.TryRestore(100, true, claims);
            progress.AddExperience(1);
            _ = progress.UnlockedTierCount;
            progress.TryClaim(0, BattlePassTrack.Free, out _);
            progress.TryClaim(0, BattlePassTrack.Premium, out _);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    private static BattlePassProgress CreateProgress() => new(new BattlePassDefinition(new[]
    {
        new BattlePassTierDefinition(100, new[] { FreeReward }, new[] { PremiumReward }),
        new BattlePassTierDefinition(300, premiumRewards: new[] { PremiumReward })
    }));
}

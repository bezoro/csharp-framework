namespace Bezoro.Progression;

/// <summary>Defines the cumulative experience threshold and independent free/premium rewards for one tier.</summary>
public sealed class BattlePassTierDefinition
{
    private readonly RewardGrant[] _freeRewards;
    private readonly RewardGrant[] _premiumRewards;

    /// <summary>Copies rewards for a positive cumulative threshold. At least one track must contain a reward.</summary>
    public BattlePassTierDefinition(long requiredExperience, ReadOnlySpan<RewardGrant> freeRewards = default, ReadOnlySpan<RewardGrant> premiumRewards = default)
    {
        if (requiredExperience <= 0) throw new ArgumentOutOfRangeException(nameof(requiredExperience));
        if (freeRewards.IsEmpty && premiumRewards.IsEmpty)
            throw new ArgumentException("A tier requires at least one reward.", nameof(freeRewards));
        RequiredExperience = requiredExperience;
        _freeRewards = RewardBatch.CopyValidated(freeRewards);
        _premiumRewards = RewardBatch.CopyValidated(premiumRewards);
    }

    /// <summary>Gets the total experience required to unlock this tier.</summary>
    public long RequiredExperience { get; }
    /// <summary>Gets the reward batch available without premium access.</summary>
    public RewardBatch FreeRewards => new(_freeRewards);
    /// <summary>Gets the reward batch requiring premium access.</summary>
    public RewardBatch PremiumRewards => new(_premiumRewards);
}

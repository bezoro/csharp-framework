namespace Bezoro.Progression;

/// <summary>A read-only batch borrowed from an immutable content definition.</summary>
/// <remarks>The definition owns the storage. Retaining this value keeps that storage alive; no copy is made on claim.</remarks>
public readonly struct RewardBatch
{
    private readonly RewardGrant[]? _rewards;

    internal RewardBatch(RewardGrant[] rewards) => _rewards = rewards;

    /// <summary>Gets the number of entitlements in the batch.</summary>
    public int Count => _rewards?.Length ?? 0;
    /// <summary>Gets the immutable entitlements. The default batch is empty.</summary>
    public ReadOnlySpan<RewardGrant> Rewards => _rewards;
    /// <summary>Gets an entitlement by index.</summary>
    public RewardGrant this[int index] => Rewards[index];

    internal static RewardGrant[] CopyValidated(ReadOnlySpan<RewardGrant> rewards)
    {
        for (int index = 0; index < rewards.Length; index++)
        {
            if (!rewards[index].Reward.IsValid || rewards[index].Quantity <= 0)
                throw new ArgumentException("Every reward must have a valid identifier and positive quantity.", nameof(rewards));
        }
        return rewards.ToArray();
    }
}

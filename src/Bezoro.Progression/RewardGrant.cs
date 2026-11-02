namespace Bezoro.Progression;

/// <summary>Describes a positive quantity of an application-defined reward.</summary>
/// <remarks>This value describes an entitlement; it does not itself grant currency, items, cosmetics, or access.</remarks>
public readonly record struct RewardGrant
{
    /// <summary>Creates an entitlement for a nondefault reward identifier and a positive quantity.</summary>
    public RewardGrant(ProgressionId reward, long quantity = 1)
    {
        if (!reward.IsValid) throw new ArgumentException("A valid reward identifier is required.", nameof(reward));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        Reward = reward;
        Quantity = quantity;
    }

    /// <summary>Gets the application-defined reward identifier.</summary>
    public ProgressionId Reward { get; }
    /// <summary>Gets the positive entitled quantity.</summary>
    public long Quantity { get; }
}

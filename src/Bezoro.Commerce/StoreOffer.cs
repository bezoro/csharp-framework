namespace Bezoro.Commerce;

/// <summary>An immutable, single-currency offer for permanent ownership of one cosmetic.</summary>
public readonly struct StoreOffer
{
    /// <summary>Creates an offer. Start is inclusive, end is exclusive; omitted bounds mean no time restriction.</summary>
    public StoreOffer(string offerId, string skinId, string currencyId, long price,
        DateTimeOffset? startsAt = null, DateTimeOffset? endsAt = null)
    {
        Identifiers.Require(offerId, nameof(offerId));
        Identifiers.Require(skinId, nameof(skinId));
        Identifiers.Require(currencyId, nameof(currencyId));
        if (price < 0) throw new ArgumentOutOfRangeException(nameof(price));
        if (startsAt.HasValue && endsAt.HasValue && startsAt.Value >= endsAt.Value)
            throw new ArgumentException("The offer must end after it starts.", nameof(endsAt));
        OfferId = offerId;
        SkinId = skinId;
        CurrencyId = currencyId;
        Price = price;
        StartsAt = startsAt;
        EndsAt = endsAt;
    }

    /// <summary>Gets the stable offer identifier; null for the default value.</summary>
    public string OfferId { get; }
    /// <summary>Gets the cosmetic granted by this offer.</summary>
    public string SkinId { get; }
    /// <summary>Gets the abstract in-game currency used to pay for this offer.</summary>
    public string CurrencyId { get; }
    /// <summary>Gets the nonnegative cost in integral currency units.</summary>
    public long Price { get; }
    /// <summary>Gets the optional inclusive start.</summary>
    public DateTimeOffset? StartsAt { get; }
    /// <summary>Gets the optional exclusive end.</summary>
    public DateTimeOffset? EndsAt { get; }
    /// <summary>Gets whether this value was initialized through its constructor.</summary>
    public bool IsValid => OfferId is not null;
    /// <summary>Checks availability using the caller's authoritative time, without reading a clock.</summary>
    public bool IsAvailableAt(DateTimeOffset now) => IsValid && (!StartsAt.HasValue || now >= StartsAt.Value) && (!EndsAt.HasValue || now < EndsAt.Value);
}

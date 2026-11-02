namespace Bezoro.Commerce;

/// <summary>A copied, immutable catalog of cosmetics and offers, safe for concurrent reads.</summary>
public sealed class StoreCatalog
{
    private readonly SkinDefinition[] _skins;
    private readonly StoreOffer[] _offers;
    private readonly Dictionary<string, int> _skinLookup = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _offerLookup = new(StringComparer.Ordinal);

    /// <summary>Copies definitions and rejects default values, duplicate identifiers and offers for unknown skins.</summary>
    public StoreCatalog(ReadOnlySpan<SkinDefinition> skins, ReadOnlySpan<StoreOffer> offers)
    {
        _skins = skins.ToArray();
        _offers = offers.ToArray();
        for (int index = 0; index < _skins.Length; index++)
        {
            var skin = _skins[index];
            if (!skin.IsValid || _skinLookup.ContainsKey(skin.SkinId))
                throw new ArgumentException("Skins must be initialized and have unique identifiers.", nameof(skins));
            _skinLookup.Add(skin.SkinId, index);
        }
        for (int index = 0; index < _offers.Length; index++)
        {
            var offer = _offers[index];
            if (!offer.IsValid || !_skinLookup.ContainsKey(offer.SkinId) || _offerLookup.ContainsKey(offer.OfferId))
                throw new ArgumentException("Offers must be initialized, unique and refer to catalog skins.", nameof(offers));
            _offerLookup.Add(offer.OfferId, index);
        }
    }

    /// <summary>Gets the immutable skin definitions in input order.</summary>
    public ReadOnlySpan<SkinDefinition> Skins => _skins;
    /// <summary>Gets the immutable offers in input order.</summary>
    public ReadOnlySpan<StoreOffer> Offers => _offers;
    /// <summary>Looks up a skin without allocating; unknown identifiers return false.</summary>
    public bool TryGetSkin(string skinId, out SkinDefinition skin)
    {
        Identifiers.Require(skinId, nameof(skinId));
        if (_skinLookup.TryGetValue(skinId, out int index)) { skin = _skins[index]; return true; }
        skin = default;
        return false;
    }
    /// <summary>Looks up an offer without allocating; unknown identifiers return false.</summary>
    public bool TryGetOffer(string offerId, out StoreOffer offer)
    {
        Identifiers.Require(offerId, nameof(offerId));
        if (_offerLookup.TryGetValue(offerId, out int index)) { offer = _offers[index]; return true; }
        offer = default;
        return false;
    }
}

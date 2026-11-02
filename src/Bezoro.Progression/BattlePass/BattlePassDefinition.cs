namespace Bezoro.Progression;

/// <summary>Shares immutable, ascending cumulative tier thresholds across players.</summary>
/// <remarks>The application identifies the season/catalog revision and decides when experience and claims are accepted.</remarks>
public sealed class BattlePassDefinition
{
    private readonly BattlePassTierDefinition[] _tiers;

    /// <summary>Copies tiers in strictly increasing experience order. An empty pass is allowed.</summary>
    public BattlePassDefinition(ReadOnlySpan<BattlePassTierDefinition> tiers)
    {
        _tiers = tiers.ToArray();
        long previous = 0;
        for (int index = 0; index < _tiers.Length; index++)
        {
            BattlePassTierDefinition tier = _tiers[index] ?? throw new ArgumentException("Tiers cannot be null.", nameof(tiers));
            if (tier.RequiredExperience <= previous)
                throw new ArgumentException("Tier thresholds must be strictly increasing.", nameof(tiers));
            previous = tier.RequiredExperience;
        }
    }

    /// <summary>Gets the immutable tiers in their ascending order.</summary>
    public ReadOnlySpan<BattlePassTierDefinition> Tiers => _tiers;
    /// <summary>Gets the number of tiers.</summary>
    public int Count => _tiers.Length;

    /// <summary>Counts unlocked tiers using a binary search. Negative experience is rejected.</summary>
    public int GetUnlockedTierCount(long experience)
    {
        if (experience < 0) throw new ArgumentOutOfRangeException(nameof(experience));
        int lower = 0;
        int upper = _tiers.Length;
        while (lower < upper)
        {
            int middle = lower + (upper - lower) / 2;
            if (_tiers[middle].RequiredExperience <= experience) lower = middle + 1;
            else upper = middle;
        }
        return lower;
    }
}

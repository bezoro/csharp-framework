namespace Bezoro.Progression;

/// <summary>Owns one player's experience, access entitlement, and per-track claim flags for a battle pass.</summary>
/// <remarks>Requires one owner or external synchronization. Experience events and premium purchases must be authorized by the host.</remarks>
public sealed class BattlePassProgress
{
    private readonly byte[] _claims;

    /// <summary>Creates a pass with zero experience, no premium access, and no claimed tiers.</summary>
    public BattlePassProgress(BattlePassDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _claims = definition.Count == 0 ? Array.Empty<byte>() : new byte[definition.Count];
    }

    /// <summary>Gets the immutable pass definition.</summary>
    public BattlePassDefinition Definition { get; }
    /// <summary>Gets cumulative earned experience.</summary>
    public long Experience { get; private set; }
    /// <summary>Gets whether premium rewards can be claimed.</summary>
    public bool HasPremiumAccess { get; private set; }
    /// <summary>Gets the number of tiers unlocked by current experience.</summary>
    public int UnlockedTierCount => Definition.GetUnlockedTierCount(Experience);

    /// <summary>Adds a positive amount, saturating at Int64.MaxValue, and returns the amount actually credited.</summary>
    public long AddExperience(long amount)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        long credited = Math.Min(amount, long.MaxValue - Experience);
        Experience += credited;
        return credited;
    }

    /// <summary>Grants premium access. Returns false if access was already granted.</summary>
    /// <remarks>This is an authoritative mutation, not payment validation. Earlier unlocked premium tiers can then be claimed.</remarks>
    public bool GrantPremiumAccess()
    {
        if (HasPremiumAccess) return false;
        HasPremiumAccess = true;
        return true;
    }

    /// <summary>Gets whether the selected reward track has already been claimed.</summary>
    public bool IsClaimed(int tierIndex, BattlePassTrack track)
    {
        ValidateTierAndTrack(tierIndex, track);
        return (_claims[tierIndex] & (1 << (int)track)) != 0;
    }

    /// <summary>Claims a nonempty, unlocked reward batch once. Premium claims additionally require premium access.</summary>
    /// <remarks>A false result leaves state unchanged. Persist state and apply returned rewards in one application transaction.</remarks>
    public bool TryClaim(int tierIndex, BattlePassTrack track, out RewardBatch rewards)
    {
        ValidateTierAndTrack(tierIndex, track);
        BattlePassTierDefinition tier = Definition.Tiers[tierIndex];
        rewards = default;
        int flag = 1 << (int)track;
        if (Experience < tier.RequiredExperience || (_claims[tierIndex] & flag) != 0 || (track == BattlePassTrack.Premium && !HasPremiumAccess))
            return false;
        RewardBatch candidate = track == BattlePassTrack.Free ? tier.FreeRewards : tier.PremiumRewards;
        if (candidate.Count == 0) return false;
        _claims[tierIndex] |= (byte)flag;
        rewards = candidate;
        return true;
    }

    /// <summary>Copies one byte per tier: bit zero denotes a free claim, bit one a premium claim.</summary>
    public void CopyClaimStateTo(Span<byte> destination)
    {
        if (destination.Length < _claims.Length) throw new ArgumentException("The destination is too short.", nameof(destination));
        _claims.AsSpan().CopyTo(destination);
    }

    /// <summary>Validates and restores a complete saved state without partial mutation.</summary>
    /// <remarks>The host must select the correct season/schema and prevent unauthorized reward-claim rollback.</remarks>
    public bool TryRestore(long experience, bool hasPremiumAccess, ReadOnlySpan<byte> claimState)
    {
        if (experience < 0 || claimState.Length != _claims.Length) return false;
        for (int index = 0; index < claimState.Length; index++)
        {
            byte claims = claimState[index];
            BattlePassTierDefinition tier = Definition.Tiers[index];
            if ((claims & ~3) != 0 || (claims != 0 && tier.RequiredExperience > experience)) return false;
            if ((claims & 1) != 0 && tier.FreeRewards.Count == 0) return false;
            if ((claims & 2) != 0 && (!hasPremiumAccess || tier.PremiumRewards.Count == 0)) return false;
        }
        Experience = experience;
        HasPremiumAccess = hasPremiumAccess;
        claimState.CopyTo(_claims);
        return true;
    }

    private void ValidateTierAndTrack(int tierIndex, BattlePassTrack track)
    {
        if (tierIndex < 0 || tierIndex >= Definition.Count) throw new ArgumentOutOfRangeException(nameof(tierIndex));
        if (track is not BattlePassTrack.Free and not BattlePassTrack.Premium) throw new ArgumentOutOfRangeException(nameof(track));
    }
}

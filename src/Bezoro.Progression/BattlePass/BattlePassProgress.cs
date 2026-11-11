namespace Bezoro.Progression;

/// <summary>Owns one player's experience, access entitlement, and per-track claim flags for a battle pass.</summary>
/// <remarks>Public operations are thread-safe and serialized per player. Experience events and premium purchases must be authorized by the host.</remarks>
public sealed class BattlePassProgress
{
    private readonly object _sync = new();
    private long _experience;
    private bool _hasPremiumAccess;
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
    public long Experience { get { lock (_sync) return _experience; } }
    /// <summary>Gets whether premium rewards can be claimed.</summary>
    public bool HasPremiumAccess { get { lock (_sync) return _hasPremiumAccess; } }
    /// <summary>Gets the number of tiers unlocked by current experience.</summary>
    public int UnlockedTierCount { get { lock (_sync) return Definition.GetUnlockedTierCount(_experience); } }

    /// <summary>Adds a positive amount, saturating at Int64.MaxValue, and returns the amount actually credited.</summary>
    public long AddExperience(long amount)
    {
        lock (_sync)
        {
            if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
            long credited = Math.Min(amount, long.MaxValue - _experience);
            _experience += credited;
            return credited;
        }
    }

    /// <summary>Grants premium access. Returns false if access was already granted.</summary>
    /// <remarks>This is an authoritative mutation, not payment validation. Earlier unlocked premium tiers can then be claimed.</remarks>
    public bool GrantPremiumAccess()
    {
        lock (_sync)
        {
            if (_hasPremiumAccess) return false;
            _hasPremiumAccess = true;
            return true;
        }
    }

    /// <summary>Gets whether the selected reward track has already been claimed.</summary>
    public bool IsClaimed(int tierIndex, BattlePassTrack track)
    {
        lock (_sync)
        {
            ValidateTierAndTrack(tierIndex, track);
            return (_claims[tierIndex] & (1 << (int)track)) != 0;
        }
    }

    /// <summary>Claims a nonempty, unlocked reward batch once. Premium claims additionally require premium access.</summary>
    /// <remarks>A false result leaves state unchanged. Persist state and apply returned rewards in one application transaction.</remarks>
    public bool TryClaim(int tierIndex, BattlePassTrack track, out RewardBatch rewards)
    {
        lock (_sync)
        {
            ValidateTierAndTrack(tierIndex, track);
            BattlePassTierDefinition tier = Definition.Tiers[tierIndex];
            rewards = default;
            int flag = 1 << (int)track;
            if (_experience < tier.RequiredExperience || (_claims[tierIndex] & flag) != 0 || (track == BattlePassTrack.Premium && !_hasPremiumAccess))
                return false;
            RewardBatch candidate = track == BattlePassTrack.Free ? tier.FreeRewards : tier.PremiumRewards;
            if (candidate.Count == 0) return false;
            _claims[tierIndex] |= (byte)flag;
            rewards = candidate;
            return true;
        }
    }

    /// <summary>Copies one byte per tier: bit zero denotes a free claim, bit one a premium claim.</summary>
    public void CopyClaimStateTo(Span<byte> destination)
    {
        lock (_sync)
        {
            if (destination.Length < _claims.Length) throw new ArgumentException("The destination is too short.", nameof(destination));
            _claims.AsSpan().CopyTo(destination);
        }
    }

    /// <summary>Copies experience, access and all claim flags from one coherent state without allocating.</summary>
    public void CopyStateTo(Span<byte> claimState, out long experience, out bool hasPremiumAccess)
    {
        lock (_sync)
        {
            if (claimState.Length < _claims.Length) throw new ArgumentException("The destination is too short.", nameof(claimState));
            _claims.AsSpan().CopyTo(claimState);
            experience = _experience;
            hasPremiumAccess = _hasPremiumAccess;
        }
    }

    /// <summary>Validates and restores a complete saved state without partial mutation.</summary>
    /// <remarks>The host must select the correct season/schema and prevent unauthorized reward-claim rollback.</remarks>
    public bool TryRestore(long experience, bool hasPremiumAccess, ReadOnlySpan<byte> claimState)
    {
        lock (_sync)
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
            _experience = experience;
            _hasPremiumAccess = hasPremiumAccess;
            claimState.CopyTo(_claims);
            return true;
        }
    }

    private void ValidateTierAndTrack(int tierIndex, BattlePassTrack track)
    {
        if (tierIndex < 0 || tierIndex >= Definition.Count) throw new ArgumentOutOfRangeException(nameof(tierIndex));
        if (track is not BattlePassTrack.Free and not BattlePassTrack.Premium) throw new ArgumentOutOfRangeException(nameof(track));
    }
}

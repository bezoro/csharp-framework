namespace Bezoro.Progression;

/// <summary>Selects the free or premium reward entitlement at a battle-pass tier.</summary>
public enum BattlePassTrack : byte
{
    /// <summary>Requires only enough earned experience.</summary>
    Free,
    /// <summary>Requires enough experience and premium access.</summary>
    Premium
}

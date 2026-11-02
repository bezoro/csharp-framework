namespace Bezoro.PlayerProfiles;

/// <summary>Immutable presentation metadata for an application-owned player identity.</summary>
/// <remarks>Authentication, display-name moderation, uniqueness and persistence belong to the host.</remarks>
public readonly struct PlayerProfile
{
    /// <summary>Creates a profile. A portrait identifier refers to a host-owned asset, never a loaded texture.</summary>
    public PlayerProfile(string playerId, string displayName, string? portraitId = null)
    {
        Identifier.Require(playerId, nameof(playerId));
        Identifier.Require(displayName, nameof(displayName));
        if (portraitId is not null) Identifier.Require(portraitId, nameof(portraitId));
        PlayerId = playerId;
        DisplayName = displayName;
        PortraitId = portraitId;
    }

    /// <summary>Gets the stable, case-sensitive application identity; null for the default value.</summary>
    public string PlayerId { get; }
    /// <summary>Gets the human-readable display name; null for the default value.</summary>
    public string DisplayName { get; }
    /// <summary>Gets an optional portrait asset identifier.</summary>
    public string? PortraitId { get; }
    /// <summary>Gets whether this value was initialized through its constructor.</summary>
    public bool IsValid => PlayerId is not null;
}

internal static class Identifier
{
    internal static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A nonempty identifier or name is required.", name);
    }
}

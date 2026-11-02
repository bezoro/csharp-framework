namespace Bezoro.Progression;

/// <summary>Describes a quest's one-way lifecycle within one progress instance.</summary>
public enum QuestStatus : byte
{
    /// <summary>The quest does not receive event counters.</summary>
    Inactive,
    /// <summary>The quest receives matching counters until all objectives are complete.</summary>
    Active,
    /// <summary>All objectives are complete and rewards remain unclaimed.</summary>
    Completed,
    /// <summary>The reward entitlements have already been claimed.</summary>
    Claimed
}

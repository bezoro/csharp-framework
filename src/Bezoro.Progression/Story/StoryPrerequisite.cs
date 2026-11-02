namespace Bezoro.Progression;

/// <summary>Requires an earlier stage to have earned at least the selected number of stars.</summary>
public readonly record struct StoryPrerequisite
{
    /// <summary>Creates a requirement for a valid stage identifier and a positive star count.</summary>
    public StoryPrerequisite(ProgressionId stage, byte requiredStars = 1)
    {
        if (!stage.IsValid) throw new ArgumentException("A valid stage identifier is required.", nameof(stage));
        if (requiredStars == 0) throw new ArgumentOutOfRangeException(nameof(requiredStars));
        Stage = stage;
        RequiredStars = requiredStars;
    }

    /// <summary>Gets the prerequisite stage identifier.</summary>
    public ProgressionId Stage { get; }
    /// <summary>Gets the minimum stars needed in that stage.</summary>
    public byte RequiredStars { get; }
}

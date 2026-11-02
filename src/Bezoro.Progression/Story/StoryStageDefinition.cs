namespace Bezoro.Progression;

/// <summary>Defines a stage and all prerequisites needed to unlock it.</summary>
/// <remarks>Gameplay, lesson text, FEN positions, and success criteria belong to application-owned content keyed by this stage identifier.</remarks>
public sealed class StoryStageDefinition
{
    private readonly StoryPrerequisite[] _prerequisites;

    /// <summary>Copies unique prerequisites. Stages without prerequisites are initially unlocked.</summary>
    public StoryStageDefinition(ProgressionId id, byte maximumStars = 3, ReadOnlySpan<StoryPrerequisite> prerequisites = default)
    {
        if (!id.IsValid) throw new ArgumentException("A valid stage identifier is required.", nameof(id));
        if (maximumStars == 0) throw new ArgumentOutOfRangeException(nameof(maximumStars));
        var stages = new HashSet<ProgressionId>();
        foreach (StoryPrerequisite prerequisite in prerequisites)
        {
            if (!prerequisite.Stage.IsValid || prerequisite.RequiredStars == 0 || prerequisite.Stage == id || !stages.Add(prerequisite.Stage))
                throw new ArgumentException("Prerequisites must reference unique, valid, other stages with positive star requirements.", nameof(prerequisites));
        }
        Id = id;
        MaximumStars = maximumStars;
        _prerequisites = prerequisites.ToArray();
    }

    /// <summary>Gets the application-defined stage identifier.</summary>
    public ProgressionId Id { get; }
    /// <summary>Gets the greatest accepted completion score.</summary>
    public byte MaximumStars { get; }
    /// <summary>Gets all requirements needed to unlock this stage.</summary>
    public ReadOnlySpan<StoryPrerequisite> Prerequisites => _prerequisites;
}

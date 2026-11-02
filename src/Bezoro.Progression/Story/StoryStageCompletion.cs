namespace Bezoro.Progression;

/// <summary>Describes an accepted improvement to a stage's best score.</summary>
public readonly struct StoryStageCompletion
{
    internal StoryStageCompletion(ProgressionId stage, byte previousStars, byte stars)
    {
        Stage = stage;
        PreviousStars = previousStars;
        Stars = stars;
    }

    /// <summary>Gets the improved stage.</summary>
    public ProgressionId Stage { get; }
    /// <summary>Gets the former best score; zero means the stage was never completed.</summary>
    public byte PreviousStars { get; }
    /// <summary>Gets the new best score.</summary>
    public byte Stars { get; }
    /// <summary>Gets whether this accepted improvement is the first completion.</summary>
    public bool IsFirstCompletion => Stage.IsValid && PreviousStars == 0;
}

using Bezoro.Progression;
using Xunit;

namespace Bezoro.Progression.Tests;

public sealed class StoryProgressTests
{
    private static readonly ProgressionId Basics = new(1);
    private static readonly ProgressionId Captures = new(2);
    private static readonly ProgressionId Checkmates = new(3);
    private static readonly ProgressionId FinalStage = new(4);

    [Fact]
    public void StageDefinitionsCopyPrerequisitesAndCatalogCopiesStageReferences()
    {
        var prerequisites = new[] { new StoryPrerequisite(Basics, 2) };
        var stage = new StoryStageDefinition(Captures, prerequisites: prerequisites);
        prerequisites[0] = new StoryPrerequisite(FinalStage);
        var stages = new[] { stage, new StoryStageDefinition(Basics) };
        var catalog = new StoryCatalog(stages);
        stages[0] = new StoryStageDefinition(FinalStage);
        Assert.Equal(Captures, catalog.Stages[0].Id);
        Assert.Equal(Basics, catalog.Stages[0].Prerequisites[0].Stage);
        Assert.Equal(2, catalog.Stages[0].Prerequisites[0].RequiredStars);
    }

    [Fact]
    public void StorySupportsBranchesAndRequiresAllPrerequisites()
    {
        StoryProgress progress = CreateProgress();
        Assert.True(progress.IsUnlocked(Basics));
        Assert.False(progress.IsUnlocked(Captures));
        Assert.False(progress.TryComplete(Captures, 3, out StoryStageCompletion rejected));
        Assert.False(rejected.IsFirstCompletion);
        Assert.True(progress.TryComplete(Basics, 1, out StoryStageCompletion first));
        Assert.True(first.IsFirstCompletion);
        Assert.Equal(Basics, first.Stage);
        Assert.Equal(0, first.PreviousStars);
        Assert.Equal(1, first.Stars);
        Assert.False(progress.IsUnlocked(Captures));
        Assert.True(progress.IsUnlocked(Checkmates));
        Assert.True(progress.TryComplete(Basics, 2, out StoryStageCompletion improved));
        Assert.False(improved.IsFirstCompletion);
        Assert.Equal(1, improved.PreviousStars);
        Assert.True(progress.IsUnlocked(Captures));
        Assert.True(progress.TryComplete(Captures, 3, out _));
        Assert.False(progress.IsUnlocked(FinalStage));
        Assert.True(progress.TryComplete(Checkmates, 1, out _));
        Assert.True(progress.IsUnlocked(FinalStage));
        Assert.True(progress.TryComplete(FinalStage, 3, out _));
        Assert.False(progress.TryComplete(FinalStage, 3, out _));
        Assert.False(progress.TryComplete(FinalStage, 1, out _));
        Assert.Equal(3, progress.GetBestStars(FinalStage));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void CompletionScoresOutsideStageRangeThrowWithoutMutation(byte stars)
    {
        StoryProgress progress = CreateProgress();
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.TryComplete(Basics, stars, out _));
        Assert.Equal(0, progress.GetBestStars(Basics));
    }

    [Fact]
    public void ContentValidationRejectsInvalidIdentifiersAndRequirements()
    {
        Assert.Throws<ArgumentException>(() => new StoryStageDefinition(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StoryStageDefinition(Basics, maximumStars: 0));
        Assert.Throws<ArgumentException>(() => new StoryStageDefinition(Basics, prerequisites: new StoryPrerequisite[] { default }));
        Assert.Throws<ArgumentException>(() => new StoryStageDefinition(Basics, prerequisites: new[] { new StoryPrerequisite(Basics) }));
        var prerequisite = new StoryPrerequisite(Basics);
        Assert.Throws<ArgumentException>(() => new StoryStageDefinition(Captures, prerequisites: new[] { prerequisite, prerequisite }));
        Assert.Throws<ArgumentException>(() => new StoryPrerequisite(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StoryPrerequisite(Basics, 0));
        Assert.Throws<ArgumentException>(() => new StoryCatalog(new StoryStageDefinition[] { null! }));
        var first = new StoryStageDefinition(Basics);
        Assert.Throws<ArgumentException>(() => new StoryCatalog(new[] { first, first }));
        Assert.Throws<ArgumentException>(() => new StoryCatalog(new[] { new StoryStageDefinition(Captures, prerequisites: new[] { prerequisite }) }));
        Assert.Throws<ArgumentException>(() => new StoryCatalog(new[] { first, new StoryStageDefinition(Captures, prerequisites: new[] { new StoryPrerequisite(Basics, 4) }) }));
    }

    [Fact]
    public void CyclesAreRejectedIncludingCycleDisconnectedFromRoot()
    {
        var cycle = new[]
        {
            new StoryStageDefinition(Basics),
            new StoryStageDefinition(Captures, prerequisites: new[] { new StoryPrerequisite(Checkmates) }),
            new StoryStageDefinition(Checkmates, prerequisites: new[] { new StoryPrerequisite(Captures) })
        };
        Assert.Throws<ArgumentException>(() => new StoryCatalog(cycle));
    }

    [Fact]
    public void DeepGraphUsesIterativeValidationAndAcceptsReverseInputOrder()
    {
        const int stageCount = 25000;
        var stages = new StoryStageDefinition[stageCount];
        stages[stageCount - 1] = new StoryStageDefinition(new ProgressionId(1));
        for (int index = 2; index <= stageCount; index++)
            stages[stageCount - index] = new StoryStageDefinition(new ProgressionId((uint)index), prerequisites: new[] { new StoryPrerequisite(new ProgressionId((uint)(index - 1))) });
        var progress = new StoryProgress(new StoryCatalog(stages));
        for (int index = 1; index <= stageCount; index++)
            Assert.True(progress.TryComplete(new ProgressionId((uint)index), 1, out _));
        Assert.Equal(1, progress.GetBestStars(new ProgressionId(stageCount)));
    }

    [Fact]
    public void RestoreRejectsInvalidGraphStateAtomicallyAndRoundTripsProgress()
    {
        StoryProgress progress = CreateProgress();
        progress.TryComplete(Basics, 2, out _);
        progress.TryComplete(Captures, 3, out _);
        var saved = new byte[4];
        progress.CopyStateTo(saved);
        Assert.Equal(new byte[] { 2, 3, 0, 0 }, saved);
        var restored = new StoryProgress(progress.Catalog);
        Assert.True(restored.TryRestore(saved));
        Assert.Equal(3, restored.GetBestStars(Captures));
        Assert.False(restored.TryRestore(new byte[3]));
        Assert.False(restored.TryRestore(new byte[] { 4, 0, 0, 0 }));
        Assert.False(restored.TryRestore(new byte[] { 1, 3, 0, 0 }));
        Assert.False(restored.TryRestore(new byte[] { 2, 3, 0, 1 }));
        Assert.Equal(3, restored.GetBestStars(Captures));
        Assert.Equal(0, restored.GetBestStars(FinalStage));
        Assert.True(restored.TryRestore(new byte[] { 2, 3, 1, 3 }));
        Assert.True(restored.IsUnlocked(FinalStage));
    }

    [Fact]
    public void EmptyGraphAndUnknownIdentifiersHaveExplicitBehavior()
    {
        var empty = new StoryProgress(new StoryCatalog(Array.Empty<StoryStageDefinition>()));
        Assert.Equal(0, empty.Catalog.Count);
        Assert.True(empty.TryRestore(Array.Empty<byte>()));
        empty.CopyStateTo(Span<byte>.Empty);
        Assert.Throws<ArgumentNullException>(() => new StoryProgress(null!));
        Assert.Throws<ArgumentException>(() => empty.IsUnlocked(Basics));
        Assert.Throws<ArgumentException>(() => empty.GetBestStars(Basics));
        Assert.Throws<ArgumentException>(() => empty.TryComplete(Basics, 1, out _));
        Assert.Throws<ArgumentException>(() => CreateProgress().CopyStateTo(new byte[1]));
    }

    [Fact]
    public void WarmedStoryUnlockCompletionAndRestoreDoNotAllocate()
    {
        StoryProgress progress = CreateProgress();
        var empty = new byte[4];
        progress.TryComplete(Basics, 2, out _);
        progress.TryRestore(empty);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++)
        {
            progress.TryComplete(Basics, 2, out _);
            _ = progress.IsUnlocked(Captures);
            progress.TryComplete(Captures, 3, out _);
            progress.TryRestore(empty);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    private static StoryProgress CreateProgress() => new(new StoryCatalog(new[]
    {
        new StoryStageDefinition(Basics),
        new StoryStageDefinition(Captures, prerequisites: new[] { new StoryPrerequisite(Basics, 2) }),
        new StoryStageDefinition(Checkmates, prerequisites: new[] { new StoryPrerequisite(Basics) }),
        new StoryStageDefinition(FinalStage, prerequisites: new[] { new StoryPrerequisite(Captures, 3), new StoryPrerequisite(Checkmates) })
    }));
}

using Bezoro.Progression;
using Xunit;

namespace Bezoro.Progression.Tests;

public sealed class ProgressionValueTests
{
    [Fact]
    public void IdentifiersHaveValueEqualityAndExplicitInvalidDefault()
    {
        Assert.Equal(new ProgressionId(7), new ProgressionId(7));
        Assert.NotEqual(new ProgressionId(7), new ProgressionId(8));
        Assert.True(new ProgressionId(uint.MaxValue).IsValid);
        Assert.False(default(ProgressionId).IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProgressionId(0));
    }

    [Fact]
    public void RewardAndObjectiveValuesValidateTheirInvariants()
    {
        var id = new ProgressionId(1);
        Assert.Equal(long.MaxValue, new RewardGrant(id, long.MaxValue).Quantity);
        Assert.Equal(long.MaxValue, new QuestObjective(id, long.MaxValue).Target);
        Assert.Throws<ArgumentException>(() => new RewardGrant(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RewardGrant(id, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RewardGrant(id, -1));
        Assert.Throws<ArgumentException>(() => new QuestObjective(default, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuestObjective(id, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuestObjective(id, -1));
    }
}

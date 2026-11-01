using Bezoro.TypingSystem.Types;

namespace Bezoro.TypingSystem.Tests.Types;

public sealed class TypingStateConstructorTests
{
	[Theory]
	[InlineData(-1, 0)]
	[InlineData(0, -1)]
	public void NegativeCounts_AreRejected(int position, int mistakes) =>
		Assert.Throws<ArgumentOutOfRangeException>(() => new TypingState(position, mistakes));

	[Fact]
	public void RestoredProgress_HasOneAcceptedCharacterCount()
	{
		var state = new TypingState(position: 1_000, mistakeCount: 70_000);
		Assert.Equal(1_000, state.Position);
		Assert.Equal(state.Position, state.CorrectCount);
		Assert.Equal(70_000, state.MistakeCount);
	}
}

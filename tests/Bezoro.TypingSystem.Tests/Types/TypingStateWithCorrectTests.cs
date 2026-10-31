using Bezoro.TypingSystem.Types;
using FluentAssertions;
using JetBrains.Annotations;

namespace Bezoro.TypingSystem.Tests.Types;

[TestSubject(typeof(TypingState))]
public class TypingStateWithCorrectTests
{
	[Fact]
	public void WithCorrect_WhenCalled_ShouldAdvancePositionAndCorrectCount()
	{
		var state = new TypingState(0);

		var updated = state.WithCorrect();

		updated.Position.Should().Be(1);
		updated.CorrectCount.Should().Be(1);
		updated.MistakeCount.Should().Be(state.MistakeCount);
	}

	[Fact]
	public void WithCorrect_WhenPositionIsAtMax_ShouldThrowInvalidOperationException()
	{
		var state = new TypingState(int.MaxValue);

		Action act = () => state.WithCorrect();

		act.Should()
		   .Throw<InvalidOperationException>()
		   .WithMessage("Position cannot exceed Int32.MaxValue.");
	}
}

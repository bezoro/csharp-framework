using Bezoro.TypingSystem.Types;
using FluentAssertions;
using JetBrains.Annotations;

namespace Bezoro.TypingSystem.Tests.Types;

[TestSubject(typeof(TypingState))]
public class TypingStateWithMistakeTests
{
	[Fact]
	public void WithMistake_WhenCalled_ShouldIncrementMistakeCount()
	{
		var state = new TypingState(0);

		var updated = state.WithMistake();

		updated.MistakeCount.Should().Be(1);
		updated.Position.Should().Be(state.Position);
		updated.CorrectCount.Should().Be(state.CorrectCount);
	}

	[Fact]
	public void WithMistake_WhenMistakeCountIsAtMaximum_ShouldThrowInvalidOperationException()
	{
		var state = new TypingState(0, int.MaxValue);

		Action act = () => state.WithMistake();

		act.Should()
		   .Throw<InvalidOperationException>()
		   .WithMessage("Mistake count cannot exceed Int32.MaxValue.");
	}
}

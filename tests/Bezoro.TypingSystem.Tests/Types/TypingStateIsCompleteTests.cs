using Bezoro.TypingSystem.Types;
using FluentAssertions;
using JetBrains.Annotations;

namespace Bezoro.TypingSystem.Tests.Types;

[TestSubject(typeof(TypingState))]
public class TypingStateIsCompleteTests
{
	[Fact]
	public void IsComplete_WhenTargetLengthIsLessThanCorrectCount_ShouldThrowArgumentOutOfRangeException()
	{
		var state = new TypingState(5);

		var action = () => state.IsComplete(4);

		action.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void IsComplete_WhenTargetLengthIsLessThanPosition_ShouldThrowArgumentOutOfRangeException()
	{
		var state = new TypingState(5);

		var action = () => state.IsComplete(3);

		action.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void IsComplete_WhenTargetLengthIsNotReached_ShouldReturnFalse()
	{
		var state = new TypingState(4);

		state.IsComplete(5).Should().BeFalse();
	}

	[Fact]
	public void IsComplete_WhenTargetLengthIsReached_ShouldReturnTrue()
	{
		var state = new TypingState(5);

		state.IsComplete(5).Should().BeTrue();
	}
}

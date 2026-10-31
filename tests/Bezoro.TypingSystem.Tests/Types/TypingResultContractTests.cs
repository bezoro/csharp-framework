using Bezoro.TypingSystem.Types;
using Bezoro.TypingSystem.Utilities;

namespace Bezoro.TypingSystem.Tests.Types;

public sealed class TypingResultContractTests
{
	[Fact]
	public void Factories_RejectContradictoryProgress()
	{
		Assert.Throws<ArgumentException>(() => TypingResult.Completed('a', 0, 'a', 5));
		Assert.Throws<ArgumentException>(() => TypingResult.Match('a', 4, 'a', 5));
		Assert.Throws<ArgumentOutOfRangeException>(() => TypingResult.Match('a', 255, 'a', 255));
		Assert.Throws<ArgumentOutOfRangeException>(() => TypingResult.Mismatch('a', -1, 'x', 5));
		Assert.Throws<ArgumentException>(() => TypingResult.PositionOutOfRange(0, 5, 'a'));
	}

	[Fact]
	public void NegativeValidationIndex_ProducesRecoverableFault()
	{
		var result = TypingValidator.ValidateInput("word", -1, 'w');
		Assert.True(result.IsFaulted);
		Assert.Equal(-1, result.Position);
		Assert.Equal(0, result.NextPosition);
	}

	[Fact]
	public void DefaultResult_DoesNotCountAsMistake()
	{
		var metrics = new TypingMetrics();
		Assert.Throws<ArgumentException>(() => metrics.Record(default));
		Assert.Equal(0u, metrics.TotalInputs);
		Assert.Equal(0u, metrics.MistakeInputs);
	}

	[Fact]
	public void LongTarget_ProgressesThroughValidatorAndState()
	{
		var word = new string('a', 1_000);
		var state = TypingState.Initial;
		while (!state.IsComplete(word.Length))
		{
			var result = TypingValidator.ValidateInput(word, state.Position, 'a');
			Assert.True(result.IsCorrect);
			state = state.WithCorrect();
			Assert.Equal(state.Position, result.NextPosition);
		}
		Assert.Equal(1_000, state.CorrectCount);
	}
}

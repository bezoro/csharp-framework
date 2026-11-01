using Bezoro.TypingSystem.Types;
using Bezoro.TypingSystem.Utilities;
using FluentAssertions;
using JetBrains.Annotations;

namespace Bezoro.TypingSystem.Tests.Utilities;

[TestSubject(typeof(TypingValidator))]
public class TypingValidatorTests
{
	[Fact]
	public void Accuracy_WhenNoAttempts_ShouldReportZero()
	{
		var metrics = new TypingMetrics();

		metrics.Accuracy.Should().Be(0d);
	}

	[Fact]
	public void ValidateInput_WhenCallbacksAreConfigured_ShouldInvokeCallbacksForMatchingStatuses()
	{
		var target = "abc".AsSpan();

		var captured = new List<TypingValidationStatus>();

		var options = new TypingValidatorOptions
		{
			OnValidated = r => captured.Add(r.Status),
			OnMatch     = r => captured.Add(TypingValidationStatus.Match),
			OnMismatch  = r => captured.Add(TypingValidationStatus.Mismatch),
			OnCompleted = r => captured.Add(TypingValidationStatus.Completed),
			OnFault     = r => captured.Add(TypingValidationStatus.PositionOutOfRange)
		};

		TypingValidator.ValidateInput(target, 0, 'a', options);
		TypingValidator.ValidateInput(target, 1, 'z', options);
		TypingValidator.ValidateInput(target, 2, 'c', options);
		TypingValidator.ValidateInput(target, 4, 'x', options);

		captured.Should().Contain(
			new[]
			{
				TypingValidationStatus.Match,
				TypingValidationStatus.Completed,
				TypingValidationStatus.Mismatch,
				TypingValidationStatus.PositionOutOfRange
			}
		);
	}

	[Fact]
	public void ValidateInput_WhenIgnoreCaseIsDisabled_ShouldRespectCase()
	{
		var target = "Abc".AsSpan();

		var result = TypingValidator.ValidateInput(target, 0, 'a');

		result.Status.Should().Be(TypingValidationStatus.Mismatch);
		result.IsCorrect.Should().BeFalse();
	}

	[Fact]
	public void ValidateInput_WhenIgnoreCaseIsEnabled_ShouldTreatDifferentCaseAsMatch()
	{
		var target = "Abc".AsSpan();

		var result = TypingValidator.ValidateInput(
			target,
			0,
			'a',
			new()
			{
				IgnoreCase = true
			}
		);

		result.Status.Should().Be(TypingValidationStatus.Match);
		result.IsCorrect.Should().BeTrue();
	}

	[Fact]
	public void ValidateInput_WhenInputCompletesTarget_ShouldReturnCompletedStatus()
	{
		var  target   = "abc".AsSpan();
		var  position = (byte)(target.Length - 1);
		char input    = target[position];

		var result = TypingValidator.ValidateInput(target, position, input);

		result.Status.Should().Be(TypingValidationStatus.Completed);
		result.IsCorrect.Should().BeTrue();
		result.IsComplete.Should().BeTrue();
		result.IsFaulted.Should().BeFalse();
		result.NextPosition.Should().Be(target.Length);
	}

	[Fact]
	public void ValidateInput_WhenInputDoesNotMatch_ShouldReturnMismatchStatus()
	{
		var        target   = "abc".AsSpan();
		const int  POSITION = 0;
		const char INPUT    = 'z';

		var result = TypingValidator.ValidateInput(target, POSITION, INPUT);

		result.Status.Should().Be(TypingValidationStatus.Mismatch);
		result.IsCorrect.Should().BeFalse();
		result.IsComplete.Should().BeFalse();
		result.IsFaulted.Should().BeFalse();
		result.Expected.Should().Be(target[POSITION]);
		result.NextPosition.Should().Be(POSITION);
	}

	[Fact]
	public void ValidateInput_WhenInputMatches_ShouldReturnMatchStatus()
	{
		var        target   = "abc".AsSpan();
		const int  POSITION = 1;
		const char INPUT    = 'b';

		var result = TypingValidator.ValidateInput(target, POSITION, INPUT);

		result.Status.Should().Be(TypingValidationStatus.Match);
		result.IsCorrect.Should().BeTrue();
		result.IsComplete.Should().BeFalse();
		result.IsFaulted.Should().BeFalse();
		result.NextPosition.Should().Be(POSITION + 1);
		result.Expected.Should().Be(target[POSITION]);
		result.TargetLength.Should().Be(target.Length);
	}

	[Fact]
	public void ValidateInput_WhenMetricsAreProvided_ShouldRecordMetrics()
	{
		var metrics = new TypingMetrics();
		var options = new TypingValidatorOptions { Metrics = metrics };
		var target  = "abc".AsSpan();

		TypingValidator.ValidateInput(target,                   0, 'a', options);
		TypingValidator.ValidateInput(target,                   1, 'z', options);
		TypingValidator.ValidateInput(ReadOnlySpan<char>.Empty, 0, 'x', options);

		metrics.TotalInputs.Should().Be(3);
		metrics.CorrectInputs.Should().Be(1);
		metrics.MistakeInputs.Should().Be(1);
		metrics.FaultedInputs.Should().Be(1);
		metrics.Accuracy.Should().Be(0.5);
	}

	[Theory]
	[InlineData(3,   2)]
	[InlineData(255, 2)]
	public void ValidateInput_WhenPositionIsOutOfRange_ShouldReturnFaultedStatus(
		byte position,
		byte expectedNextPosition)
	{
		var        target = "abc".AsSpan();
		const char INPUT  = 'a';

		var result = TypingValidator.ValidateInput(target, position, INPUT);

		result.Status.Should().Be(TypingValidationStatus.PositionOutOfRange);
		result.IsFaulted.Should().BeTrue();
		result.IsCorrect.Should().BeFalse();
		result.IsComplete.Should().BeFalse();
		result.NextPosition.Should().Be(expectedNextPosition);
		result.TargetLength.Should().Be(target.Length);
	}

	[Fact]
	public void ValidateInput_WhenTargetExceedsByteLength_ShouldValidateFullSpan()
	{
		string word = new('a', 1_000);
		var result = TypingValidator.ValidateInput(word.AsSpan(), 999, 'a');
		result.Status.Should().Be(TypingValidationStatus.Completed);
		result.NextPosition.Should().Be(1_000);
	}

	[Fact]
	public void ValidateInput_WhenTargetIsEmpty_ShouldReturnEmptyTargetStatus()
	{
		var        target = ReadOnlySpan<char>.Empty;
		const char INPUT  = 'x';

		var result = TypingValidator.ValidateInput(target, 0, INPUT);

		result.Status.Should().Be(TypingValidationStatus.EmptyTarget);
		result.IsFaulted.Should().BeTrue();
		result.IsCorrect.Should().BeFalse();
		result.IsComplete.Should().BeFalse();
		result.NextPosition.Should().Be(0);
		result.TargetLength.Should().Be(0);
	}

	[Fact]
	public void ValidateInput_WhenTargetLengthEqualsMaximum_ShouldValidateSuccessfully()
	{
		string     word     = new('a', byte.MaxValue);
		var        target   = word.AsSpan();
		const byte POSITION = 0;
		const char INPUT    = 'a';

		var result = TypingValidator.ValidateInput(target, POSITION, INPUT);

		result.Status.Should().Be(TypingValidationStatus.Match);
		result.TargetLength.Should().Be(byte.MaxValue);
		result.NextPosition.Should().Be(POSITION + 1);
	}
}

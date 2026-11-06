using FluentAssertions;

namespace Bezoro.Chess.UCI.Protocol.Tests.API.Types;

public sealed class PlayableMatchClockAuthoringTests
{
	[Fact]
	public void FromClampedSeconds_NormalizesNegativeIncrementAndDelayBeforeValidation()
	{
		var control = PlayableMatchTimeControl.FromClampedSeconds(300, -5, -2);
		control.InitialTime.Should().Be(TimeSpan.FromSeconds(300));
		control.IncrementPerMove.Should().Be(TimeSpan.Zero);
		control.DelayPerMove.Should().Be(TimeSpan.Zero);
		control.Invoking(value => value.Validate()).Should().NotThrow();
	}

	[Fact]
	public void FromClampedSeconds_RestoresPausedStateTimestampAndClampsRemainingTimes()
	{
		var timestamp = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
		var setup = PlayableMatchClockSetup.FromClampedSeconds(120.5, -90, -2, false, timestamp);
		setup.WhiteRemaining.Should().Be(TimeSpan.FromSeconds(120.5));
		setup.BlackRemaining.Should().Be(TimeSpan.Zero);
		setup.DelayRemaining.Should().Be(TimeSpan.Zero);
		setup.IsPaused.Should().BeFalse();
		setup.SnapshotUtc.Should().Be(timestamp);
		setup.ExactRestore.Should().BeNull();
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(double.NegativeInfinity)]
	public void TimeControl_NonpositiveInitialTimeIsRejectedByTheFramework(double seconds)
	{
		Action create = () => PlayableMatchTimeControl.FromClampedSeconds(seconds);
		create.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("InitialTime");
	}

	[Fact]
	public void NegativeInfinity_IsClampedForOptionalAndSnapshotTimes()
	{
		PlayableMatchTimeControl.FromClampedSeconds(300, double.NegativeInfinity).IncrementPerMove.Should().Be(TimeSpan.Zero);
		PlayableMatchClockSetup.FromClampedSeconds(double.NegativeInfinity, 0).WhiteRemaining.Should().Be(TimeSpan.Zero);
	}

	[Theory]
	[InlineData(double.NaN)]
	[InlineData(double.PositiveInfinity)]
	[InlineData(double.MaxValue)]
	public void NonrepresentableSeconds_AreNeverSilentlyAccepted(double seconds)
	{
		Action control = () => PlayableMatchTimeControl.FromClampedSeconds(300, seconds);
		Action snapshot = () => PlayableMatchClockSetup.FromClampedSeconds(300, seconds);
		if (double.IsNaN(seconds))
		{
			control.Should().Throw<ArgumentException>();
			snapshot.Should().Throw<ArgumentException>();
		}
		else
		{
			control.Should().Throw<OverflowException>();
			snapshot.Should().Throw<OverflowException>();
		}
	}

	[Fact]
	public void InvalidTimeoutPolicy_IsRejectedByTheFrameworkFactory()
	{
		Action create = () => PlayableMatchTimeControl.FromClampedSeconds(300, timeoutPolicy: (PlayableMatchTimeoutPolicy)99);
		create.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("TimeoutPolicy");
	}
}

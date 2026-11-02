using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Types;

namespace Bezoro.Chess.UCI.Tests.API.Types;

public sealed class UciCoordinatorOptionsTests
{
	[Fact]
	public void ParameterlessConstruction_UsesTheDocumentedDefaults()
	{
		var options = new UciCoordinatorOptions();
		Assert.Equal(UciCoordinatorOptions.Default, options);
		options.Validate();
		using var session = new UciGameEngineSession(options: options);
		Assert.Equal(20, session.LegalMoves.Count);
		Assert.False(options.EnableBackgroundAnalysis);
	}

	[Theory]
	[InlineData("default")]
	[InlineData("threads")]
	[InlineData("variations")]
	[InlineData("depth")]
	[InlineData("time")]
	[InlineData("claims")]
	[InlineData("offers")]
	[InlineData("fallback")]
	public void InvalidSettings_FailBeforeStartingASession(string setting)
	{
		var defaults = UciCoordinatorOptions.Default;
		var options = setting switch
		{
			"default" => default,
			"threads" => defaults with { PonderThreads = 0 },
			"variations" => defaults with { MultiPv = 0 },
			"depth" => defaults with { ClassificationDepth = 0 },
			"time" => defaults with { EngineMoveTimeMs = -1 },
			"claims" => defaults with { ClaimableDrawPolicy = (PlayableMatchClaimableDrawPolicy)99 },
			"offers" => defaults with { DrawOfferPolicy = (PlayableMatchDrawOfferPolicy)99 },
			_ => defaults with { ControlledMoveFallbackPolicy = (PlayableMatchControlledMoveFallbackPolicy)99 }
		};
		Assert.Throws<ArgumentOutOfRangeException>(() => new UciGameEngineSession(options: options));
	}
}

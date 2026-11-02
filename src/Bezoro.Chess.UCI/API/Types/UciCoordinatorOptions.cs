using Bezoro.Chess.UCI.Protocol.API.Types;

namespace Bezoro.Chess.UCI.API.Types;

/// <summary>
///     Configuration options for the UCI Coordinator.
/// </summary>
public readonly record struct UciCoordinatorOptions(
	int                                 PonderThreads                  = 2,
	int                                 MultiPv                        = 1,
	uint                                ClassificationDepth            = 6,
	int                                 EngineMoveTimeMs               = 1_000,
	PlayableMatchTimeControl?           TimeControl                    = null,
	PlayableMatchClaimableDrawPolicy    ClaimableDrawPolicy            = PlayableMatchClaimableDrawPolicy.Automatic,
	PlayableMatchDrawOfferPolicy        DrawOfferPolicy                = PlayableMatchDrawOfferPolicy.ExpireOnMove,
	PlayableMatchControlledMoveFallbackPolicy ControlledMoveFallbackPolicy = PlayableMatchControlledMoveFallbackPolicy.UseLocalFallback,
	bool EnableBackgroundAnalysis = false
)
{
	/// <summary>Creates the documented defaults, including finite search budgets and disabled automatic analysis.</summary>
	public UciCoordinatorOptions() : this(PonderThreads: 2) { }

	/// <summary>
	///     Gets the default configuration options.
	///     The parameterless constructor produces the same value. An uninitialized default value is invalid.
	/// </summary>
	public static UciCoordinatorOptions Default { get; } = new();

	/// <summary>Rejects invalid resource budgets, draw policies, and clock settings before a session uses them.</summary>
	public void Validate()
	{
		if (PonderThreads <= 0) throw new ArgumentOutOfRangeException(nameof(PonderThreads));
		if (MultiPv <= 0) throw new ArgumentOutOfRangeException(nameof(MultiPv));
		if (ClassificationDepth == 0) throw new ArgumentOutOfRangeException(nameof(ClassificationDepth));
		if (EngineMoveTimeMs <= 0) throw new ArgumentOutOfRangeException(nameof(EngineMoveTimeMs));
		if (ClaimableDrawPolicy is not (PlayableMatchClaimableDrawPolicy.Automatic or PlayableMatchClaimableDrawPolicy.ClaimRequired))
			throw new ArgumentOutOfRangeException(nameof(ClaimableDrawPolicy));
		if (DrawOfferPolicy is not (PlayableMatchDrawOfferPolicy.ExpireOnMove or PlayableMatchDrawOfferPolicy.PersistUntilResponse))
			throw new ArgumentOutOfRangeException(nameof(DrawOfferPolicy));
		if (ControlledMoveFallbackPolicy is not (PlayableMatchControlledMoveFallbackPolicy.UseLocalFallback or PlayableMatchControlledMoveFallbackPolicy.Throw))
			throw new ArgumentOutOfRangeException(nameof(ControlledMoveFallbackPolicy));
		TimeControl?.Validate();
	}
}

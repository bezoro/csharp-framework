using System;
using Bezoro.Core.Types;
using Bezoro.ECS.Types;

namespace Bezoro.GameSystems.HealthSystem.Types;

/// <summary>
///     Deferred mutation request consumed by <see cref="Services.HealthSystem" />.
/// </summary>
public readonly struct HealthMutationRequest
{
	/// <summary>
	///     Initializes a mutation request.
	/// </summary>
	/// <param name="targetEntity">Entity whose <see cref="Health" /> component will be modified.</param>
	/// <param name="kind">Mutation operation.</param>
	/// <param name="value">Amount or target value for the operation.</param>
	/// <param name="maxUpdateMode">Mode used when <paramref name="kind" /> is <see cref="HealthMutationKind.SetMax" />.</param>
	public HealthMutationRequest(
		Entity             targetEntity,
		HealthMutationKind kind,
		uint               value,
		MaxValueUpdateMode maxUpdateMode = MaxValueUpdateMode.ClampCurrent)
	{
		if (kind < HealthMutationKind.Damage || kind > HealthMutationKind.IncreaseHealth)
			throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported mutation kind.");
		if (maxUpdateMode is not (MaxValueUpdateMode.ClampCurrent or MaxValueUpdateMode.PreservePercentage))
			throw new ArgumentOutOfRangeException(nameof(maxUpdateMode), maxUpdateMode, "Unsupported max update mode.");
		TargetEntity = targetEntity;
		Kind = kind;
		Value = value;
		MaxUpdateMode = maxUpdateMode;
	}

	/// <summary>
	///     Gets mode used when setting max health.
	/// </summary>
	public MaxValueUpdateMode MaxUpdateMode { get; }

	/// <summary>
	///     Gets target entity to mutate.
	/// </summary>
	public Entity TargetEntity { get; }

	/// <summary>
	///     Gets requested mutation operation.
	/// </summary>
	public HealthMutationKind Kind { get; }

	/// <summary>
	///     Gets amount or target value used by the operation.
	/// </summary>
	public uint Value { get; }
}

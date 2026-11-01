using System;
using System.Numerics;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Bezoro.GameSystems.MovementSystem.Types;
using Bezoro.GameSystems.StreamingSystem.Types;

namespace Bezoro.GameSystems.StreamingSystem.Services;

/// <summary>
///     ECS system that applies distance-based streaming transitions.
/// </summary>
[Reads(typeof(Position))]
[Writes(typeof(StreamState))]
[ReadsResource(typeof(StreamingConfig))]
[WritesResource(typeof(StreamingRuntimeState))]
[WritesResource(typeof(StreamingEventsResource))]
public sealed class StreamingSystem : ISystem
{
	private const float DEFAULT_STREAM_IN_DISTANCE    = 100f;
	private const float DEFAULT_STREAM_OUT_DISTANCE   = 120f;
	private const int   DEFAULT_MAX_ENTITIES_PER_TICK = 50;
	private QueryHandle<StreamingQuery> _query;

	/// <summary>
	///     Raised when an entity changes streaming state.
	/// </summary>
	public event Action<StreamingStateChangedEvent>? Changed;

	/// <summary>
	///     Raised when an entity transitions to streamed-in.
	/// </summary>
	public event Action<StreamingStateChangedEvent>? StreamedIn;

	/// <summary>
	///     Raised when an entity transitions to streamed-out.
	/// </summary>
	public event Action<StreamingStateChangedEvent>? StreamedOut;

	/// <inheritdoc />
	public Stage Stage => Stage.Tick;

	/// <inheritdoc />
	public SystemLoopPhase LoopPhase => SystemLoopPhase.Tick;

	/// <inheritdoc />
	public SystemUpdateSettings UpdateSettings => SystemUpdateSettings.EveryTick;

	/// <inheritdoc />
	public void OnCreate(World world)
	{
		if (world is null) throw new ArgumentNullException(nameof(world));

		EnsureResources(world);
		_query = world.Compile<StreamingQuery>();
	}

	/// <inheritdoc />
	public void Update(in SystemContext context)
	{
		var world = context.World;
		if (world is null) throw new ArgumentNullException(nameof(world));

		EnsureResources(world);

		ref readonly var config = ref world.ReadResource<StreamingConfig>();
		ValidateConfig(in config);

		using var cursor = world.Execute(_query);
		cursor.MoveNext();
		int total = cursor.Count;
		if (total == 0)
		{
			ref var emptyRuntime = ref world.WriteResource<StreamingRuntimeState>();
			emptyRuntime.NextEntityIndex = 0;
			return;
		}

		int maxEntitiesPerTick = config.MaxEntitiesPerTick <= 0
									 ? total
									 : Math.Min(config.MaxEntitiesPerTick, total);

		ref var runtime               = ref world.WriteResource<StreamingRuntimeState>();
		int     startIndex            = NormalizeStartIndex(runtime.NextEntityIndex, total);
		double inDistanceSquared  = (double)config.StreamInDistance * config.StreamInDistance;
		double outDistanceSquared = (double)config.StreamOutDistance * config.StreamOutDistance;
		var   referencePosition  = config.ReferencePosition;

		for (int i = 0, index = startIndex; i < maxEntitiesPerTick; i++, index++)
		{
			if (index == total) index = 0;
			ref readonly var position = ref cursor.Read<Position>(index);
			if (!IsFinite(position.X) || !IsFinite(position.Y) || !IsFinite(position.Z)) continue;
			double distanceSquared = GetDistanceSquared(referencePosition, in position);
			var state = cursor.Read<StreamState>(index);
			var transition = TryTransition(ref state, distanceSquared, inDistanceSquared, outDistanceSquared);
			if (!transition.HasValue) continue;
			cursor.Write<StreamState>(index) = state;
			Publish(world, cursor.GetEntity(index), transition.Value, distanceSquared);
		}

		runtime.NextEntityIndex = maxEntitiesPerTick >= total - startIndex
			? maxEntitiesPerTick - (total - startIndex)
			: startIndex + maxEntitiesPerTick;
	}

	private static double GetDistanceSquared(Vector3 referencePosition, in Position entityPosition)
	{
		double dx = (double)referencePosition.X - entityPosition.X;
		double dy = (double)referencePosition.Y - entityPosition.Y;
		double dz = (double)referencePosition.Z - entityPosition.Z;
		return dx * dx + dy * dy + dz * dz;
	}

	private static int NormalizeStartIndex(int startIndex, int total)
	{
		if (startIndex < 0 || startIndex >= total)
			return 0;

		return startIndex;
	}

	private static StreamingTransition? TryTransition(
		ref StreamState streamState,
		double           distanceSquared,
		double           inDistanceSquared,
		double           outDistanceSquared)
	{
		if (!streamState.IsStreamedIn && distanceSquared <= inDistanceSquared)
		{
			streamState.IsStreamedIn = true;
			return StreamingTransition.StreamedIn;
		}

		if (streamState.IsStreamedIn && distanceSquared > outDistanceSquared)
		{
			streamState.IsStreamedIn = false;
			return StreamingTransition.StreamedOut;
		}

		return null;
	}

	private static void EnsureResources(World world)
	{
		world.GetOrCreateResource(static () => new StreamingConfig
			{
				ReferencePosition  = Vector3.Zero,
				StreamInDistance   = DEFAULT_STREAM_IN_DISTANCE,
				StreamOutDistance  = DEFAULT_STREAM_OUT_DISTANCE,
				MaxEntitiesPerTick = DEFAULT_MAX_ENTITIES_PER_TICK
			}
		);

		world.GetOrCreateResource<StreamingRuntimeState>();
		world.GetOrCreateResource<StreamingEventsResource>();
	}

	private static void ValidateConfig(in StreamingConfig config)
	{
		if (!IsFinite(config.ReferencePosition.X) || !IsFinite(config.ReferencePosition.Y) || !IsFinite(config.ReferencePosition.Z))
			throw new ArgumentOutOfRangeException(nameof(config), "ReferencePosition must be finite.");

		if (!IsFinite(config.StreamInDistance) || config.StreamInDistance < 0f)
			throw new ArgumentOutOfRangeException(
				nameof(config), "StreamInDistance must be non-negative and finite."
			);

		if (!IsFinite(config.StreamOutDistance) || config.StreamOutDistance < config.StreamInDistance)
			throw new ArgumentException(
				$"StreamOutDistance ({config.StreamOutDistance}) must be >= StreamInDistance ({config.StreamInDistance}) to prevent flickering.",
				nameof(config)
			);
	}

	private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

	private Action<StreamingStateChangedEvent>? GetHandler(StreamingTransition transition) =>
		transition switch
		{
			StreamingTransition.StreamedIn  => StreamedIn,
			StreamingTransition.StreamedOut => StreamedOut,
			_                               => null
		};

	private void Publish(
		World               world,
		Entity              targetEntity,
		StreamingTransition transition,
		double               distanceSquared)
	{
		var     eventData = new StreamingStateChangedEvent(targetEntity, transition, distanceSquared);
		ref var events    = ref world.GetOrCreateResource<StreamingEventsResource>();
		events.Enqueue(in eventData);

		try
		{
			Changed?.Invoke(eventData);
		}
		catch
		{
			// Keep the transition-specific notification independent from Changed failures.
		}

		try
		{
			GetHandler(transition)?.Invoke(eventData);
		}
		catch
		{
			// Event handler exceptions should not break simulation.
		}
	}
}

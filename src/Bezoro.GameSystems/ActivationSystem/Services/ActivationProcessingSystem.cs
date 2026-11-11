using System;
using System.Collections.Generic;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Bezoro.GameSystems.ActivationSystem.Types;

namespace Bezoro.GameSystems.ActivationSystem.Services;

/// <summary>
///     ECS system that applies activation cancellation, ordering, and per-tick activation budget.
/// </summary>
[Writes(typeof(ActivationEntry))]
[Reads(typeof(ActivationCancellationRequest))]
[ReadsResource(typeof(ActivationConfig))]
[WritesResource(typeof(ActivationRuntimeState))]
[WritesResource(typeof(ActivationEventsResource))]
[WritesResource(typeof(ActivationDispatchQueueResource))]
public sealed class ActivationProcessingSystem : ISystem
{
	/// <summary>
	///     Raised when all pending entries have been activated.
	/// </summary>
	public event Action<ActivationCompletedEvent>? Completed;

	/// <inheritdoc />
	public Stage Stage => Stage.Tick;

	/// <inheritdoc />
	public SystemLoopPhase LoopPhase => SystemLoopPhase.Tick;

	/// <inheritdoc />
	public void OnCreate(World world)
	{
		if (world is null) throw new ArgumentNullException(nameof(world));
		EnsureResources(world);
	}

	/// <inheritdoc />
	public void Update(in SystemContext context)
	{
		var world = context.World;
		if (world is null) throw new ArgumentNullException(nameof(world));
		EnsureResources(world);

		if (world.Query<ActivationCancellationQuery>().Any())
		{
			var entriesByHandle = BuildEntryIndex(world);
			ApplyCancellations(world, context.CommandStream, entriesByHandle);
		}

		var pendingEntries = CollectPendingEntries(world, out var activatedCount);
		int pendingEntryCount = pendingEntries?.Count ?? 0;
		ref var runtime = ref world.WriteResource<ActivationRuntimeState>();
		if (pendingEntryCount > 0)
			runtime.CompletionPublished = false;

		if (pendingEntryCount > 1)
			pendingEntries!.Sort(static (left, right) =>
				{
					var priorityComparison = right.Priority.CompareTo(left.Priority);
					return priorityComparison != 0
							   ? priorityComparison
							   : left.HandleId.CompareTo(right.HandleId);
				}
			);

		ref readonly var config = ref world.ReadResource<ActivationConfig>();
		ref var dispatchQueue = ref world.WriteResource<ActivationDispatchQueueResource>();

		var maxActivationsPerTick = config.MaxActivationsPerTick <= 0
										? int.MaxValue
										: config.MaxActivationsPerTick;
		var targetActivationCount = Math.Min(maxActivationsPerTick, pendingEntryCount);

		var activatedThisTick = 0;
		for (var i = 0; i < targetActivationCount; i++)
		{
			var candidate = pendingEntries![i];
			if (!world.TryWrite<ActivationEntry>(candidate.EntryEntity, out var entryRef))
				continue;

			ref var entry = ref entryRef.Value;
			var activatedEntry = entry;
			if (!activatedEntry.TryActivate(out var callback))
				continue;

			// Keep the original entry pending, including its callback, until queue ownership succeeds.
			if (!dispatchQueue.TryEnqueue(callback))
				break;
			entry = activatedEntry;
			activatedThisTick++;
		}

		activatedCount += activatedThisTick;
		var pendingCount = pendingEntryCount - activatedThisTick;
		runtime.SetCounts(activatedCount, pendingCount);

		PublishCompletionIfNeeded(world, runtime);
	}

	private void PublishCompletionIfNeeded(World world, ActivationRuntimeState runtime)
	{
		if (runtime.PendingCount == 0 && runtime.ActivatedCount > 0)
		{
			if (runtime.CompletionPublished)
				return;

			var eventData = new ActivationCompletedEvent(runtime.ActivatedCount);
			ref var events = ref world.GetOrCreateResource<ActivationEventsResource>();
			events.Enqueue(in eventData);

			try
			{
				Completed?.Invoke(eventData);
			}
			catch
			{
				// Event handler exceptions should not break simulation.
			}

			runtime.CompletionPublished = true;
			return;
		}

		runtime.CompletionPublished = false;
	}

	private static List<PendingActivationCandidate>? CollectPendingEntries(World world, out int activatedCount)
	{
		List<PendingActivationCandidate>? pendingEntries = null;
		var activatedCountLocal = 0;

		world.Query<ActivationEntryQuery>().ForEachRead<ActivationEntry>(
			(Entity entryEntity, in ActivationEntry entry) =>
			{
				if (!entry.Handle.IsValid) return;
				if (entry.State == ActivationState.Activated)
				{
					activatedCountLocal++;
					return;
				}

				if (entry.State != ActivationState.Pending)
					return;

				(pendingEntries ??= new()).Add(new(entryEntity, entry.Priority, entry.Handle.Id));
			}
		);

		activatedCount = activatedCountLocal;
		return pendingEntries;
	}

	private static Dictionary<int, Entity> BuildEntryIndex(World world)
	{
		var entriesByHandle = new Dictionary<int, Entity>();
		world.Query<ActivationEntryQuery>().ForEachRead<ActivationEntry>(
			(Entity entryEntity, in ActivationEntry entry) =>
			{
				if (!entry.Handle.IsValid)
					return;

				entriesByHandle[entry.Handle.Id] = entryEntity;
			}
		);

		return entriesByHandle;
	}

	private static void ApplyCancellations(
		World                         world,
		CommandStream                 commands,
		IReadOnlyDictionary<int, Entity> entriesByHandle)
	{
		world.Query<ActivationCancellationQuery>().ForEachRead<ActivationCancellationRequest>(
			(Entity requestEntity, in ActivationCancellationRequest request) =>
			{
				if (entriesByHandle.TryGetValue(request.Handle.Id, out var entryEntity) &&
					world.TryWrite<ActivationEntry>(entryEntity, out var entryRef) &&
					entryRef.Value.State == ActivationState.Pending)
				{
					entryRef.Value.CancelPending();
				}

				commands.Destroy(requestEntity);
			}
		);
	}

	private static void EnsureResources(World world)
	{
		world.GetOrCreateResource<ActivationConfig>();
		world.GetOrCreateResource<ActivationRuntimeState>();
		world.GetOrCreateResource<ActivationEventsResource>();
		world.GetOrCreateResource<ActivationDispatchQueueResource>();
	}

}

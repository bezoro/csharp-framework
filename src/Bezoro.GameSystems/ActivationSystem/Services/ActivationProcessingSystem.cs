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

		ref readonly var config = ref world.ReadResource<ActivationConfig>();
		ref var dispatchQueue = ref world.WriteResource<ActivationDispatchQueueResource>();
		int budget = config.MaxActivationsPerTick <= 0 ? int.MaxValue : config.MaxActivationsPerTick;
		budget = Math.Min(budget, dispatchQueue.MaxCapacity - dispatchQueue.Count);
		var pendingEntries = CollectPendingEntries(world, budget, out var activatedCount, out var pendingEntryCount,
			out var best);
		ref var runtime = ref world.WriteResource<ActivationRuntimeState>();
		if (pendingEntryCount > 0) runtime.CompletionPublished = false;
		pendingEntries?.Sort(CompareCandidates);
		int targetActivationCount = Math.Min(budget, pendingEntryCount);

		var activatedThisTick = 0;
		for (var i = 0; i < targetActivationCount; i++)
		{
			var candidate = budget == 1 ? best : pendingEntries![i];
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
			if (!events.TryEnqueue(in eventData)) return;

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

	private static int CompareCandidates(PendingActivationCandidate left, PendingActivationCandidate right)
	{
		int priority = right.Priority.CompareTo(left.Priority);
		return priority != 0 ? priority : left.HandleId.CompareTo(right.HandleId);
	}

	private static List<PendingActivationCandidate>? CollectPendingEntries(World world, int budget, out int activatedCount,
		out int pendingCount, out PendingActivationCandidate best)
	{
		List<PendingActivationCandidate>? pendingEntries = null;
		var activatedCountLocal = 0;
		int pending = 0;
		PendingActivationCandidate highest = default;

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

				pending++;
				if (budget == 0) return;
				var candidate = new PendingActivationCandidate(entryEntity, entry.Priority, entry.Handle.Id);
				if (budget == 1)
				{
					if (pending == 1 || CompareCandidates(candidate, highest) < 0) highest = candidate;
				}
				else
				{
					(pendingEntries ??= new()).Add(candidate);
					// Once two batches accumulate, the lower-ranked half can never enter this tick's batch.
					if (pendingEntries.Count - budget >= budget)
					{
						pendingEntries.Sort(CompareCandidates);
						pendingEntries.RemoveRange(budget, pendingEntries.Count - budget);
					}
				}
			}
		);

		activatedCount = activatedCountLocal;
		pendingCount = pending;
		best = highest;
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

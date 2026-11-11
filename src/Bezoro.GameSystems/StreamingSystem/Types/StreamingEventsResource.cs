using System;
using Bezoro.GameSystems.Internal;

namespace Bezoro.GameSystems.StreamingSystem.Types;

/// <summary>
///     Thread-safe resource queue used to consume streaming transitions.
/// </summary>
public sealed class StreamingEventsResource
{
	private readonly SynchronizedEventQueue<StreamingStateChangedEvent> _events;

	/// <summary>Creates an initially empty thread-safe queue that grows with its backlog.</summary>
	public StreamingEventsResource() : this(0, int.MaxValue) { }

	/// <summary>Creates thread-safe retained queue storage with optional preallocation and a backlog limit.</summary>
	/// <param name="initialCapacity">Number of payload slots to preallocate.</param>
	/// <param name="maxCapacity">Maximum retained payloads. The default grows on demand without a smaller application limit.</param>
	public StreamingEventsResource(int initialCapacity = 0, int maxCapacity = int.MaxValue) =>
		_events = new(initialCapacity, maxCapacity);

	/// <summary>Gets the configured maximum retained payload count.</summary>
	public int MaxCapacity => _events.MaxCapacity;

	/// <summary>
	///     Gets the current queued event count; concurrent operations may immediately change it.
	/// </summary>
	public int Count => _events.Count;

	/// <summary>
	///     Enqueues a streaming event.
	/// </summary>
	/// <param name="eventData">Event payload to enqueue.</param>
	/// <exception cref="InvalidOperationException">The configured queue capacity is exhausted.</exception>
	public void Enqueue(in StreamingStateChangedEvent eventData) => _events.Enqueue(eventData);

	/// <summary>Attempts to enqueue without changing the queue when its configured capacity is full.</summary>
	/// <param name="eventData">Payload to retain.</param>
	/// <returns>Whether the payload was queued.</returns>
	public bool TryEnqueue(in StreamingStateChangedEvent eventData) => _events.TryEnqueue(eventData);

	/// <summary>
	///     Attempts to dequeue the next streaming event.
	/// </summary>
	/// <param name="eventData">Dequeued event payload when available.</param>
	/// <returns><c>true</c> when an event was dequeued; otherwise <c>false</c>.</returns>
	public bool TryDequeue(out StreamingStateChangedEvent eventData) => _events.TryDequeue(out eventData);

	/// <summary>
	///     Clears all queued events.
	/// </summary>
	public void Clear() => _events.Clear();
}

using Bezoro.Events.Types;

namespace Bezoro.Events.Abstractions;

/// <summary>
///     Type-safe event bus supporting synchronous dispatch, queued dispatch, and priority-ordered handlers.
/// </summary>
public interface IEventBus : IDisposable
{
	/// <summary>
	///     The number of events currently in the queue.
	/// </summary>
	int QueuedCount { get; }

	/// <summary>Gets the maximum pending event count, or -1 for unbounded queuing.</summary>
	int MaximumQueuedEvents { get; }

	/// <summary>
	///     The total number of active subscriptions across all event types.
	/// </summary>
	int SubscriptionCount { get; }

	/// <summary>
	///     Removes a subscription by its handle.
	/// </summary>
	/// <returns><c>true</c> if the subscription was found and removed; otherwise <c>false</c>.</returns>
	bool Unsubscribe(SubscriptionHandle handle);

	/// <summary>
	///     Publishes an event synchronously, invoking all matching handlers inline.
	/// </summary>
	/// <returns>The context after all handlers have run (check <see cref="EventContext{TEvent}.Handled" />).</returns>
	EventContext<TEvent> Publish<TEvent>(TEvent evt) where TEvent : struct;

	/// <summary>
	///     Dispatches at most the queued count captured at call entry. Callback-posted work waits for a later pass
	///     unless concurrent consumers remove earlier work. Handler work and nested flushes have separate costs.
	/// </summary>
	/// <returns>The number of events dispatched.</returns>
	int FlushQueued();

	/// <summary>
	///     Dequeues at most <paramref name="maxEvents" /> events, including events without handlers
	///     or with failed dispatch. Callback-enqueued events can consume the remaining budget.
	///     Nested flushes have independent budgets and counts. This does not limit elapsed time or handler work.
	/// </summary>
	/// <returns>The number of events directly dequeued by this call.</returns>
	/// <exception cref="ObjectDisposedException">The bus is disposed, regardless of the budget.</exception>
	/// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEvents" /> is negative.</exception>
	int FlushQueued(int maxEvents);

	/// <summary>
	///     Subscribes a handler to events of type <typeparamref name="TEvent" />.
	///     Higher priority handlers run first. Same-priority handlers run in subscription order.
	/// </summary>
	/// <returns>A handle that can be used to unsubscribe.</returns>
	SubscriptionHandle Subscribe<TEvent>(Action<EventContext<TEvent>> handler, int priority = 0)
		where TEvent : struct;

	/// <summary>
	///     Enqueues an event for deferred dispatch via <see cref="FlushQueued()" />.
	///     Throws <see cref="InvalidOperationException" /> when a configured capacity is full.
	/// </summary>
	void Enqueue<TEvent>(TEvent evt) where TEvent : struct;

	/// <summary>Enqueues without invoking handlers, returning false when the configured capacity is full.</summary>
	/// <remarks>A rejected event allocates no dispatch envelope. A disposed bus throws rather than rejecting silently.</remarks>
	bool TryEnqueue<TEvent>(TEvent evt) where TEvent : struct;
}

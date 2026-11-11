using System.Buffers;
using System.Collections.Concurrent;
using Bezoro.Events.Abstractions;
using Bezoro.Events.Types;

namespace Bezoro.Events.Services;

/// <summary>
///     Thread-safe event bus with priority-ordered handlers, cancellable propagation,
///     synchronous inline dispatch, and queued deferred dispatch.
/// </summary>
public sealed class EventBus : IEventBus
{
	private readonly Action<Exception>? _reportHandlerError;
	private readonly ConcurrentDictionary<int, Type>                     _handleToType  = new();
	private readonly ConcurrentDictionary<Type, List<EventSubscription>> _subscriptions = new();
	private readonly Queue<QueuedEvent>                                  _queue         = new();
	private readonly object                                             _lifecycleGate = new();
	private          int                                                 _disposed;
	private static   long                                                _nextId;

	/// <summary>Creates a bus without handler-error reporting.</summary>
	public EventBus() : this(-1) { }

	/// <summary>Creates a bus with reporting for exceptions caught from handlers.</summary>
	/// <param name="reportHandlerError">Runs inline after a handler fails. Reporter failures are ignored.</param>
	public EventBus(Action<Exception>? reportHandlerError) : this(-1, reportHandlerError) { }

	/// <summary>Creates a bus with a queued-event admission limit.</summary>
	/// <param name="maximumQueuedEvents">Maximum pending events; zero disables queuing and -1 is unbounded.</param>
	/// <param name="reportHandlerError">Optional inline reporter for handler failures.</param>
	public EventBus(int maximumQueuedEvents, Action<Exception>? reportHandlerError = null)
	{
		if (maximumQueuedEvents < -1) throw new ArgumentOutOfRangeException(nameof(maximumQueuedEvents));
		MaximumQueuedEvents = maximumQueuedEvents;
		_reportHandlerError = reportHandlerError;
	}

	/// <inheritdoc />
	public int QueuedCount { get { lock (_lifecycleGate) return _queue.Count; } }

	/// <inheritdoc />
	public int MaximumQueuedEvents { get; }

	/// <inheritdoc />
	public int SubscriptionCount
	{
		get
		{
			var count = 0;
			foreach (var pair in _subscriptions)
			{
				lock (pair.Value)
				{
					count += pair.Value.Count;
				}
			}

			return count;
		}
	}

	/// <inheritdoc />
	public bool Unsubscribe(SubscriptionHandle handle)
	{
		lock (_lifecycleGate)
		{
			ThrowIfDisposed();

			if (!_handleToType.TryRemove(handle.Id, out var eventType))
				return false;

			if (!_subscriptions.TryGetValue(eventType, out var list))
				return false;

			lock (list)
			{
				for (var i = 0; i < list.Count; i++)
				{
					if (list[i].Handle != handle)
						continue;

					list.RemoveAt(i);
					return true;
				}
			}

			return false;
		}
	}

	/// <inheritdoc />
	public EventContext<TEvent> Publish<TEvent>(TEvent evt) where TEvent : struct
	{
		ThrowIfDisposed();

		var context = new EventContext<TEvent>(evt);
		(var snapshot, int count) = RentSnapshot(typeof(TEvent));

		if (snapshot is null)
			return context;

		try
		{
			for (var i = 0; i < count; i++)
			{
				if (context.Handled)
					break;

				try
				{
					((Action<EventContext<TEvent>>)snapshot[i].Handler)(context);
				}
				catch (Exception error)
				{
					try { _reportHandlerError?.Invoke(error); }
					catch { /* Error reporting must not interrupt the remaining handlers. */ }
				}
			}
		}
		finally
		{
			ArrayPool<EventSubscription>.Shared.Return(snapshot, true);
		}

		return context;
	}

	/// <inheritdoc />
	public int FlushQueued()
	{
		int initialCount;
		lock (_lifecycleGate) { ThrowIfDisposed(); initialCount = _queue.Count; }
		return FlushQueuedCore(initialCount);
	}

	/// <inheritdoc />
	public int FlushQueued(int maxEvents)
	{
		ThrowIfDisposed();
		if (maxEvents < 0) throw new ArgumentOutOfRangeException(nameof(maxEvents));
		return FlushQueuedCore(maxEvents);
	}

	private int FlushQueuedCore(int maxEvents)
	{
		var count = 0;
		while (count < maxEvents)
		{
			QueuedEvent queued;
			lock (_lifecycleGate)
			{
				if (_queue.Count == 0) break;
				queued = _queue.Dequeue();
			}
			try { queued.Dispatch(); }
			catch { /* Dispatch failure does not retain the event or stop this pass. */ }
			count++;
		}
		return count;
	}

	/// <inheritdoc />
	public SubscriptionHandle Subscribe<TEvent>(Action<EventContext<TEvent>> handler, int priority = 0)
		where TEvent : struct
	{
		lock (_lifecycleGate)
		{
			ThrowIfDisposed();

			if (handler is null)
				throw new ArgumentNullException(nameof(handler));

			long nextId = Interlocked.Increment(ref _nextId);
			if (nextId > int.MaxValue)
				throw new InvalidOperationException("The event subscription identifier space is exhausted.");

			int id           = (int)nextId;
			var handle       = new SubscriptionHandle(id);
			var subscription = new EventSubscription(handle, priority, handler);

			var eventType = typeof(TEvent);
			var list      = _subscriptions.GetOrAdd(eventType, _ => new());
			lock (list)
			{
				InsertSorted(list, subscription);
			}

			_handleToType[id] = eventType;

			return handle;
		}
	}

	/// <inheritdoc />
	public void Dispose()
	{
		lock (_lifecycleGate)
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
				return;

			foreach (var pair in _subscriptions)
			{
				lock (pair.Value)
				{
					pair.Value.Clear();
				}
			}

			_subscriptions.Clear();
			_handleToType.Clear();

			_queue.Clear();
		}
	}

	/// <inheritdoc />
	public void Enqueue<TEvent>(TEvent evt) where TEvent : struct
	{
		if (!TryEnqueue(evt)) throw new InvalidOperationException("The event queue has reached its configured capacity.");
	}

	/// <inheritdoc />
	public bool TryEnqueue<TEvent>(TEvent evt) where TEvent : struct
	{
		lock (_lifecycleGate)
		{
			ThrowIfDisposed();
			if (MaximumQueuedEvents >= 0 && _queue.Count >= MaximumQueuedEvents) return false;
			_queue.Enqueue(new QueuedEvent<TEvent>(this, evt));
			return true;
		}
	}

	private static void InsertSorted(List<EventSubscription> list, EventSubscription subscription)
	{
		// Descending by priority; same priority appends (stable insertion order)
		var index = 0;
		while (index < list.Count && list[index].Priority >= subscription.Priority) index++;

		list.Insert(index, subscription);
	}

	private (EventSubscription[]? Array, int Count) RentSnapshot(Type eventType)
	{
		if (!_subscriptions.TryGetValue(eventType, out var list))
			return (null, 0);

		lock (list)
		{
			int count = list.Count;
			if (count == 0)
				return (null, 0);

			var rented = ArrayPool<EventSubscription>.Shared.Rent(count);
			list.CopyTo(rented, 0);
			return (rented, count);
		}
	}

	private void ThrowIfDisposed()
	{
		if (Volatile.Read(ref _disposed) != 0)
			throw new ObjectDisposedException(nameof(EventBus));
	}
}

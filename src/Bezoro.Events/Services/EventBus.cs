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
	private readonly ConcurrentQueue<QueuedEvent>                        _queue         = new();
	private readonly object                                             _lifecycleGate = new();
	private          int                                                 _disposed;
	private static   long                                                _nextId;

	/// <summary>Creates a bus without handler-error reporting.</summary>
	public EventBus() { }

	/// <summary>Creates a bus with reporting for exceptions caught from handlers.</summary>
	/// <param name="reportHandlerError">Runs inline after a handler fails. Reporter failures are ignored.</param>
	public EventBus(Action<Exception>? reportHandlerError) => _reportHandlerError = reportHandlerError;

	/// <inheritdoc />
	public int QueuedCount => _queue.Count;

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
		ThrowIfDisposed();

		var count = 0;
		while (_queue.TryDequeue(out var queued))
		{
			try
			{
				queued.Dispatch();
			}
			catch
			{
				// Don't let dispatch exceptions stop the flush
			}

			count++;
		}

		return count;
	}

	/// <inheritdoc />
	public int FlushQueued(int maxEvents)
	{
		ThrowIfDisposed();

		if (maxEvents < 0)
			throw new ArgumentOutOfRangeException(nameof(maxEvents));

		var count = 0;
		while (count < maxEvents && _queue.TryDequeue(out var queued))
		{
			try
			{
				queued.Dispatch();
			}
			catch
			{
				// Don't let dispatch exceptions stop the flush
			}

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

			while (_queue.TryDequeue(out _)) { }
		}
	}

	/// <inheritdoc />
	public void Enqueue<TEvent>(TEvent evt) where TEvent : struct
	{
		lock (_lifecycleGate)
		{
			ThrowIfDisposed();

			_queue.Enqueue(new QueuedEvent<TEvent>(this, evt));
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

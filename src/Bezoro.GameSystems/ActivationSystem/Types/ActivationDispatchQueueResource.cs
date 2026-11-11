using System;
using Bezoro.GameSystems.Internal;

namespace Bezoro.GameSystems.ActivationSystem.Types;

/// <summary>
///     Thread-safe resource queue containing callbacks ready for dispatch.
/// </summary>
public sealed class ActivationDispatchQueueResource
{
	private readonly SynchronizedEventQueue<Action> _callbacks;

	/// <summary>Creates an initially empty thread-safe queue that grows with its backlog.</summary>
	public ActivationDispatchQueueResource() : this(0, int.MaxValue) { }

	/// <summary>Creates thread-safe retained queue storage with optional preallocation and a backlog limit.</summary>
	/// <param name="initialCapacity">Number of payload slots to preallocate.</param>
	/// <param name="maxCapacity">Maximum retained payloads. The default grows on demand without a smaller application limit.</param>
	public ActivationDispatchQueueResource(int initialCapacity = 0, int maxCapacity = int.MaxValue) =>
		_callbacks = new(initialCapacity, maxCapacity);

	/// <summary>Gets the configured maximum retained payload count.</summary>
	public int MaxCapacity => _callbacks.MaxCapacity;

	/// <summary>
	///     Gets the number of queued callbacks.
	/// </summary>
	public int Count => _callbacks.Count;

	/// <summary>
	///     Clears all queued callbacks.
	/// </summary>
	public void Clear() => _callbacks.Clear();

	/// <summary>
	///     Enqueues a callback for dispatch.
	/// </summary>
	/// <param name="callback">Callback to enqueue.</param>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="callback" /> is null.</exception>
	/// <exception cref="InvalidOperationException">The configured queue capacity is exhausted.</exception>
	public void Enqueue(Action callback)
	{
		if (callback is null) throw new ArgumentNullException(nameof(callback));
		_callbacks.Enqueue(callback);
	}

	/// <summary>Attempts to enqueue without changing the queue when its configured capacity is full.</summary>
	/// <param name="callback">Payload to retain.</param>
	/// <returns>Whether the payload was queued.</returns>
	public bool TryEnqueue(Action callback)
	{
		if (callback is null) throw new ArgumentNullException(nameof(callback));
		return _callbacks.TryEnqueue(callback);
	}

	/// <summary>
	///     Attempts to dequeue the next callback.
	/// </summary>
	/// <param name="callback">Dequeued callback when available.</param>
	/// <returns><c>true</c> when a callback was dequeued; otherwise <c>false</c>.</returns>
	public bool TryDequeue(out Action callback) => _callbacks.TryDequeue(out callback);
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Bezoro.Core.Abstractions;
using Bezoro.Core.Extensions;
using Bezoro.Core.Internal;

namespace Bezoro.Core.Types.Pool;

/// <summary>
///     A high-performance, thread-safe object pool supporting configurable policies,
///     async waiting, and automatic capacity management.
/// </summary>
/// <typeparam name="T">The type of objects to pool. Must be a reference type.</typeparam>
[DebuggerDisplay("Available={AvailableCount}, Total={TotalCount}, Max={MaxCapacity}")]
public sealed class ObjectPool<T> : IPool<T>, IDisposable where T : class
{
	// Reuse storage for available references. All access after construction holds
	// _lifecycleGate; unlike ConcurrentStack, returning an item allocates no node.
	private Stack<T> _available;
	private readonly ConcurrentDictionary<T, PoolItemState> _ownedItems;
	private readonly object                                  _lifecycleGate = new();
	private readonly IPoolPolicy<T>                          _policy;
	private readonly PoolOptions                             _options;
	private readonly SemaphoreSlim?                          _asyncWaitSemaphore;
	private readonly CancellationTokenSource?                _disposeWaitCancellation;
	private int _activeWaiters;
	private bool _waitShutdownComplete;
	private bool _waitResourcesDisposed;

	private int  _disposed;
	private int  _totalCount;
	private long _totalAsyncWaits;
	private long _totalCreated;
	private long _totalDiscarded;
	private long _totalRented;
	private long _totalReturned;
	private long _totalTimeouts;

	/// <summary>
	///     Initializes a new pool with the specified factory and default options.
	/// </summary>
	/// <param name="factory">Factory function to create new instances.</param>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="factory" /> is <c>null</c>.</exception>
	public ObjectPool(Func<T> factory)
		: this(new PoolPolicy<T>(factory), PoolOptions.Default) { }

	/// <summary>
	///     Initializes a new pool with the specified factory and options.
	/// </summary>
	/// <param name="factory">Factory function to create new instances.</param>
	/// <param name="options">Pool configuration options.</param>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="factory" /> is <c>null</c>.</exception>
	public ObjectPool(Func<T> factory, PoolOptions options)
		: this(new PoolPolicy<T>(factory), options) { }

	/// <summary>
	///     Initializes a new pool with full policy control.
	/// </summary>
	/// <param name="policy">The lifecycle policy for pooled objects.</param>
	/// <param name="options">Pool configuration options.</param>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="policy" /> is <c>null</c>.</exception>
	public ObjectPool(IPoolPolicy<T> policy, PoolOptions options = default)
	{
		_policy     = policy.ThrowIfNull();
		_options    = options.IsConfigured || options != default ? options : PoolOptions.Default;
		if (_options.InitialCapacity < 0) throw new ArgumentOutOfRangeException(nameof(options), "Initial capacity must be nonnegative.");
		if (_options.MaxCapacity < -1) throw new ArgumentOutOfRangeException(nameof(options), "Maximum capacity must be -1 or nonnegative.");
		if (_options.MaxCapacity >= 0 && _options.InitialCapacity > _options.MaxCapacity)
			throw new ArgumentException("Initial capacity must not exceed maximum capacity.", nameof(options));
		_available  = new(_options.InitialCapacity);
		_ownedItems = new(ReferenceIdentityComparer<T>.Instance);

		if (_options.EnableAsyncWait && _options.MaxCapacity > 0)
		{
			_asyncWaitSemaphore = new(0, _options.MaxCapacity);
			_disposeWaitCancellation = new();
		}

		try
		{
			PrewarmPool();
		}
		catch
		{
			try { Dispose(); }
			catch { /* Preserve the creation failure after releasing prewarmed objects. */ }
			throw;
		}
	}

	/// <inheritdoc />
	public int AvailableCount { get { lock (_lifecycleGate) return _available.Count; } }

	/// <inheritdoc />
	public int MaxCapacity => _options.MaxCapacity;

	/// <inheritdoc />
	public int TotalCount => Volatile.Read(ref _totalCount);

	/// <inheritdoc />
	public PoolStatistics Statistics => BuildStatistics();

	/// <inheritdoc />
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool Return(T item)
	{
		item.ThrowIfNull();

		// Claim the object before callbacks can reset, publish, or discard it.
		// A foreign return owns an uncounted Returning marker until capacity is reserved.
		while (true)
		{
			if (_ownedItems.TryGetValue(item, out var state))
			{
				if (state != PoolItemState.Rented)
					return false;
				if (_ownedItems.TryUpdate(item, PoolItemState.Returning, PoolItemState.Rented))
					break;
				continue;
			}

			// Unknown objects cannot transfer ownership to a disposed pool. In particular,
			// a duplicate late return must not dispose a previously released object again.
			if (Volatile.Read(ref _disposed) != 0)
				return false;
			if (!_ownedItems.TryAdd(item, PoolItemState.Returning))
				continue;

			if (TryReserveCapacity())
				break;

			try { DiscardUnownedItem(item); }
			finally { TryRemoveOwnedItem(item, PoolItemState.Returning); }
			return false;
		}

		if (Volatile.Read(ref _disposed) != 0)
		{
			ReleaseOwnedItem(item);
			return false;
		}

		bool canReturn;
		try { canReturn = ResetAndValidateForReturn(item); }
		catch
		{
			try { ReleaseOwnedItem(item); }
			catch { /* Preserve the reset failure. */ }
			throw;
		}

		if (!canReturn || Volatile.Read(ref _disposed) != 0)
		{
			ReleaseOwnedItem(item);
			return false;
		}

		return _ownedItems.TryUpdate(item, PoolItemState.Available, PoolItemState.Returning) &&
			   PublishReturnedItem(item);
	}

	/// <inheritdoc />
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool TryRent([NotNullWhen(true)] out T? item) => TryAcquire(false, out item);

	/// <inheritdoc />
	public int TrimExcess(Percent targetUtilization = default)
	{
		if (targetUtilization == default)
			targetUtilization = Percent.Ninety;

		var trimmed   = 0;
		int available = AvailableCount;
		int total     = TotalCount;

		if (total == 0) return 0;

		var targetAvailable = (int)((long)total * (100 - targetUtilization.Value) / 100);
		int toRemove        = available - targetAvailable;

		while (toRemove > 0 && TryPopAvailable(out var item))
		{
			if (!_ownedItems.TryGetValue(item, out var state) || state != PoolItemState.Available)
				continue;

			ReleaseOwnedItem(item);
			trimmed++;
			toRemove--;
		}

		return trimmed;
	}

	/// <inheritdoc />
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public PooledObjectHandle<T> RentHandle()
	{
		var item = Rent();
		return new(item, this);
	}

	/// <inheritdoc />
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public T Rent()
	{
		ThrowIfDisposed();
		return RentCore();
	}

	/// <inheritdoc />
	public async ValueTask<T?> RentAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		if (timeout != Timeout.InfiniteTimeSpan && (timeout < TimeSpan.Zero ||
			timeout.Ticks > (long)int.MaxValue * TimeSpan.TicksPerMillisecond))
			throw new ArgumentOutOfRangeException(nameof(timeout));

		long startedAt = Stopwatch.GetTimestamp();

		while (true)
		{
			ThrowIfDisposed();
			cancellationToken.ThrowIfCancellationRequested();
			if (TryAcquire(true, out var item))
				return item;

			if (_asyncWaitSemaphore is null || !_options.EnableAsyncWait)
				return null;

			var remaining = timeout == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan
				: timeout - TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - startedAt) / (double)Stopwatch.Frequency);
			if (timeout != Timeout.InfiniteTimeSpan && remaining <= TimeSpan.Zero)
			{
				if (_options.TrackStatistics)
					Interlocked.Increment(ref _totalTimeouts);

				return null;
			}

			if (_options.TrackStatistics)
				Interlocked.Increment(ref _totalAsyncWaits);

			bool acquired = await WaitForAvailableAsync(remaining, cancellationToken).ConfigureAwait(false);
			if (!acquired)
			{
				if (_options.TrackStatistics)
					Interlocked.Increment(ref _totalTimeouts);

				return null;
			}

			// Loop to try again - another thread may have grabbed the item
		}
	}

	/// <inheritdoc />
	public async ValueTask<T> RentAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();

		while (true)
		{
			ThrowIfDisposed();
			cancellationToken.ThrowIfCancellationRequested();
			if (TryAcquire(true, out var item))
				return item;

			if (_asyncWaitSemaphore is null || !_options.EnableAsyncWait)
				throw new PoolExhaustedException(typeof(T), _options.MaxCapacity);

			if (_options.TrackStatistics)
				Interlocked.Increment(ref _totalAsyncWaits);

			// Wait for an item to be returned, then loop to try again
			await WaitForAvailableAsync(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <inheritdoc />
	public void Clear()
	{
		Stack<T> clearing;
		lock (_lifecycleGate)
		{
			if (_available.Count == 0) return;
			clearing = _available;
			_available = new Stack<T>();
		}
		ExceptionDispatchInfo? failure = null;
		while (clearing.Count != 0)
		{
			try { ReleaseOwnedItem(clearing.Pop()); }
			catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
		}
		failure?.Throw();
	}

	/// <summary>
	///     Disposes the pool and all managed resources.
	/// </summary>
	public void Dispose()
	{
		lock (_lifecycleGate)
		{
			if (Volatile.Read(ref _disposed) != 0)
				return;

			Volatile.Write(ref _disposed, 1);
		}

		try
		{
			Clear();
		}
		finally
		{
			try
			{
				_disposeWaitCancellation?.Cancel();
			}
			finally
			{
				lock (_lifecycleGate)
				{
					_waitShutdownComplete = true;
					DisposeWaitResourcesIfReady();
				}
			}
		}
	}

	private async Task<bool> WaitForAvailableAsync(TimeSpan timeout, CancellationToken cancellationToken)
	{
		CancellationTokenSource linkedCancellation;
		lock (_lifecycleGate)
		{
			ThrowIfDisposed();
			// A return or discard may have happened after TryAcquire failed but before
			// this waiter registered. Recheck under the same gate as publication/signals.
			if (_available.Count != 0 || TotalCount < _options.MaxCapacity)
				return true;
			linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken, _disposeWaitCancellation!.Token);
			_activeWaiters++;
		}

		try
		{
			return await _asyncWaitSemaphore!.WaitAsync(timeout, linkedCancellation.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0)
		{
			throw new ObjectDisposedException(nameof(ObjectPool<T>));
		}
		finally
		{
			linkedCancellation.Dispose();
			lock (_lifecycleGate)
			{
				_activeWaiters--;
				DisposeWaitResourcesIfReady();
			}
		}
	}

	private void DisposeWaitResourcesIfReady()
	{
		if (!_waitShutdownComplete || _activeWaiters != 0 || _waitResourcesDisposed)
			return;

		_waitResourcesDisposed = true;
		_asyncWaitSemaphore?.Dispose();
		_disposeWaitCancellation?.Dispose();
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void CompleteRent(T item)
	{
		try
		{
			if (item is IPooledObject pooled)
				pooled.OnRent();
		}
		catch
		{
			try
			{
				try
				{
					ReleaseOwnedItem(item);
				}
				finally
				{
					lock (_lifecycleGate)
					{
						if (Volatile.Read(ref _disposed) == 0)
							TrySignalWaiters();
					}
				}
			}
			catch
			{
				// Cleanup or notification failure must not replace the OnRent exception.
			}

			throw;
		}

		// Validation and OnRent observe an in-progress acquisition. Return must not
		// publish that item until its lease is ready to leave the pool.
		if (!_ownedItems.TryUpdate(item, PoolItemState.Rented, PoolItemState.Renting))
			throw new InvalidOperationException("The object could not complete its rental transition.");
		IncrementRented();
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool ResetAndValidateForReturn(T item) => _policy.Reset(item);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void DiscardUnownedItem(T item)
	{
		try
		{
			_policy.OnDiscard(item);
		}
		finally
		{
			if (_options.TrackStatistics)
				Interlocked.Increment(ref _totalDiscarded);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void IncrementRented()
	{
		if (_options.TrackStatistics)
			Interlocked.Increment(ref _totalRented);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void IncrementReturned()
	{
		if (_options.TrackStatistics)
			Interlocked.Increment(ref _totalReturned);
	}

	private void PrewarmPool()
	{
		for (var i = 0; i < _options.InitialCapacity; i++)
		{
			if (!TryCreateOwnedItem(out var item))
				break;

			if (!_ownedItems.TryUpdate(item, PoolItemState.Available, PoolItemState.Renting))
			{
				ReleaseOwnedItem(item);
				throw new InvalidOperationException("Failed to register a prewarmed item as available.");
			}

			_available.Push(item);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool PublishReturnedItem(T item)
	{
		lock (_lifecycleGate)
		{
			if (Volatile.Read(ref _disposed) == 0)
			{
				_available.Push(item);
				IncrementReturned();
				TrySignalWaiters();
				return true;
			}
		}

		ReleaseOwnedItem(item);
		return false;
	}

	private bool ReleaseOwnedItem(T item)
	{
		while (true)
		{
			if (!_ownedItems.TryGetValue(item, out var state) || state == PoolItemState.Discarding)
				return false;
			if (_ownedItems.TryUpdate(item, PoolItemState.Discarding, state))
				break;
		}

		try
		{
			_policy.OnDiscard(item);
		}
		finally
		{
			// Keep the marker while user disposal code runs so reentrant and concurrent
			// returns cannot reset or dispose the same object during that callback.
			TryRemoveOwnedItem(item, PoolItemState.Discarding);
			Interlocked.Decrement(ref _totalCount);
			if (_options.TrackStatistics) Interlocked.Increment(ref _totalDiscarded);
			lock (_lifecycleGate)
			{
				if (Volatile.Read(ref _disposed) == 0)
					TrySignalWaiters();
			}
		}

		return true;
	}

	private bool TryAcquire(bool allowCreate, [NotNullWhen(true)] out T? item)
	{
		if (Volatile.Read(ref _disposed) != 0)
		{
			item = null;
			return false;
		}

		while (TryPopAvailable(out var availableItem))
		{
			if (!_ownedItems.TryUpdate(availableItem, PoolItemState.Renting, PoolItemState.Available))
				continue;

			bool valid;
			try
			{
				valid = !_options.ValidateOnRent || _policy.Validate(availableItem);
			}
			catch
			{
				try { ReleaseOwnedItem(availableItem); }
				catch { /* Preserve the validation failure. */ }
				throw;
			}

			if (!valid)
			{
				ReleaseOwnedItem(availableItem);
				continue;
			}

			item = availableItem;
			CompleteRent(item);
			return true;
		}

		if (!allowCreate)
		{
			item = null;
			return false;
		}

		if (!TryCreateOwnedItem(out item))
			return false;

		CompleteRent(item);
		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryCreateOwnedItem([NotNullWhen(true)] out T? item)
	{
		item = null;
		if (!TryReserveCapacity())
			return false;

		try
		{
			var created = _policy.Create();
			if (created is null)
				throw new InvalidOperationException("Pool policy returned null from Create().");

			if (!_ownedItems.TryAdd(created, PoolItemState.Renting))
				throw new InvalidOperationException("Pool policy returned an instance already owned by the pool.");

			item = created;

			if (_options.TrackStatistics)
				Interlocked.Increment(ref _totalCreated);

			return true;
		}
		catch
		{
			Interlocked.Decrement(ref _totalCount);
			lock (_lifecycleGate)
			{
				if (Volatile.Read(ref _disposed) == 0)
					TrySignalWaiters();
			}
			throw;
		}
	}


	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryRemoveOwnedItem(T item, PoolItemState state) =>
		((ICollection<KeyValuePair<T, PoolItemState>>)_ownedItems).Remove(new(item, state));

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool TryReserveCapacity()
	{
		while (true)
		{
			int currentCount = Volatile.Read(ref _totalCount);

			if ((_options.MaxCapacity >= 0 && currentCount >= _options.MaxCapacity) || currentCount == int.MaxValue)
				return false;

			if (Interlocked.CompareExchange(ref _totalCount, currentCount + 1, currentCount) == currentCount)
				return true;
		}
	}

	private PoolStatistics BuildStatistics()
	{
		int  total       = TotalCount;
		int  available   = AvailableCount;
		// Counts are sampled independently under concurrent use. Bound that approximate
		// snapshot before converting, and widen before multiplying large capacities.
		long rented      = Math.Max(0, total - (long)available);
		byte utilization = total > 0 ? (byte)Math.Min(100, rented * 100 / total) : (byte)0;

		return new()
		{
			TotalRented     = Volatile.Read(ref _totalRented),
			TotalReturned   = Volatile.Read(ref _totalReturned),
			TotalCreated    = Volatile.Read(ref _totalCreated),
			TotalDiscarded  = Volatile.Read(ref _totalDiscarded),
			TotalAsyncWaits = Volatile.Read(ref _totalAsyncWaits),
			TotalTimeouts   = Volatile.Read(ref _totalTimeouts),
			Utilization     = new(utilization > 100 ? (byte)100 : utilization)
		};
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private T RentCore()
	{
		if (TryAcquire(true, out var item)) return item;
		throw new PoolExhaustedException(typeof(T), _options.MaxCapacity);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ThrowIfDisposed()
	{
		if (Volatile.Read(ref _disposed) != 0)
			throw new ObjectDisposedException(nameof(ObjectPool<T>));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void TrySignalWaiters()
	{
		// Callers hold _lifecycleGate. Limit signals to registered waiters and the
		// semaphore's bound, so normal returns never throw SemaphoreFullException.
		if (_asyncWaitSemaphore is null || _activeWaiters == 0 ||
			_asyncWaitSemaphore.CurrentCount >= Math.Min(_activeWaiters, _options.MaxCapacity))
			return;

		_asyncWaitSemaphore.Release();
	}

	private bool TryPopAvailable([NotNullWhen(true)] out T? item)
	{
		lock (_lifecycleGate)
		{
			if (_available.Count != 0)
			{
				item = _available.Pop();
				return true;
			}
		}
		item = null;
		return false;
	}
}

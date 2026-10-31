using Bezoro.Events.Abstractions;

namespace Bezoro.Events.Services;

/// <summary>
///     Default implementation of <see cref="IUnityEventBuses" /> backed by <see cref="EventBus" /> instances.
/// </summary>
/// <remarks>
///     Creates Unity event buses backed by the provided <see cref="IEventBus" /> instances.
/// </remarks>
/// <param name="update">The Update bus.</param>
/// <param name="fixedUpdate">The FixedUpdate bus.</param>
/// <param name="lateUpdate">The LateUpdate bus.</param>
/// <param name="ownsBuses">Whether this instance should dispose the provided buses.</param>
public sealed class UnityEventBuses(
	IEventBus update,
	IEventBus fixedUpdate,
	IEventBus lateUpdate,
	bool      ownsBuses = true
) : IUnityEventBuses
{
	private readonly bool _ownsBuses = ownsBuses;
	private int _disposed;

	/// <summary>
	///     Creates Unity event buses backed by new <see cref="EventBus" /> instances.
	/// </summary>
	public UnityEventBuses()
		: this(new EventBus(), new EventBus(), new EventBus()) { }

	/// <inheritdoc />
	public IEventBus FixedUpdate { get; } = fixedUpdate ?? throw new ArgumentNullException(nameof(fixedUpdate));

	/// <inheritdoc />
	public IEventBus LateUpdate { get; } = lateUpdate ?? throw new ArgumentNullException(nameof(lateUpdate));

	/// <inheritdoc />
	public IEventBus Update { get; } = update ?? throw new ArgumentNullException(nameof(update));

	/// <inheritdoc />
	public int FlushFixedUpdate() { ThrowIfDisposed(); return FixedUpdate.FlushQueued(); }

	/// <inheritdoc />
	public int FlushFixedUpdate(int maxEvents) { ThrowIfDisposed(); return FixedUpdate.FlushQueued(maxEvents); }

	/// <inheritdoc />
	public int FlushLateUpdate() { ThrowIfDisposed(); return LateUpdate.FlushQueued(); }

	/// <inheritdoc />
	public int FlushLateUpdate(int maxEvents) { ThrowIfDisposed(); return LateUpdate.FlushQueued(maxEvents); }

	/// <inheritdoc />
	public int FlushUpdate() { ThrowIfDisposed(); return Update.FlushQueued(); }

	/// <inheritdoc />
	public int FlushUpdate(int maxEvents) { ThrowIfDisposed(); return Update.FlushQueued(maxEvents); }

	/// <inheritdoc />
	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0 || !_ownsBuses)
			return;

		List<Exception>? errors = null;
		DisposeBus(Update, ref errors);
		if (!ReferenceEquals(FixedUpdate, Update))
			DisposeBus(FixedUpdate, ref errors);
		if (!ReferenceEquals(LateUpdate, Update) && !ReferenceEquals(LateUpdate, FixedUpdate))
			DisposeBus(LateUpdate, ref errors);
		if (errors != null) throw new AggregateException("One or more event buses failed to dispose.", errors);
	}

	private static void DisposeBus(IEventBus bus, ref List<Exception>? errors)
	{
		try { bus.Dispose(); }
		catch (Exception error) { (errors ??= new()).Add(error); }
	}

	private void ThrowIfDisposed()
	{
		if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(UnityEventBuses));
	}
}

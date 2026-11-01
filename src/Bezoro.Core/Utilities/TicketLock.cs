namespace Bezoro.Core.Utilities;

/// <summary>
///     Provides FIFO ticket-based ordering for coordinated access across multiple callers.
/// </summary>
/// <remarks>
///     Thread-safe. Use <see cref="ExecuteOrdered" /> to serialize mutations that must be ordered
///     relative to each other.
/// </remarks>
public sealed class TicketLock
{
	private long _nextTicket;
	private long _servingTicket = 1;

	/// <summary>Gets the synchronization object used by this ticket lock.</summary>
	public object SyncRoot { get; } = new();

	/// <summary>Executes an action under FIFO ticket ordering.</summary>
	/// <param name="action">Action to execute.</param>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="action" /> is null.</exception>
	public void ExecuteOrdered(Action action)
	{
		if (action is null)
			throw new ArgumentNullException(nameof(action));

		if (Monitor.IsEntered(SyncRoot))
		{
			action();
			return;
		}

		long ticket = Interlocked.Increment(ref _nextTicket);
		lock (SyncRoot)
		{
			while (ticket != _servingTicket)
				Monitor.Wait(SyncRoot);

			try
			{
				action();
			}
			finally
			{
				_servingTicket = unchecked(_servingTicket + 1);
				Monitor.PulseAll(SyncRoot);
			}
		}
	}
}

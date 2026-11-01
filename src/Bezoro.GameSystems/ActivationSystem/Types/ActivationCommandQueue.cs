using System;
using System.Collections.Concurrent;
using System.Threading;
using Bezoro.GameSystems.ActivationSystem.Abstractions;

namespace Bezoro.GameSystems.ActivationSystem.Types;

/// <summary>
///     Thread-safe ingress queue for activation register/cancel commands.
/// </summary>
public sealed class ActivationCommandQueue : IActivationCommandSink
{
	private readonly ConcurrentQueue<ActivationCommand> _pendingCommands = new();
	private static   long                               _nextHandleId;

	/// <summary>
	///     Gets an approximate count of pending ingress commands.
	/// </summary>
	public int PendingCommandCount => _pendingCommands.Count;

	/// <inheritdoc />
	public bool Cancel(ActivationHandle handle)
	{
		if (!handle.IsValid)
			return false;

		_pendingCommands.Enqueue(ActivationCommand.Cancel(handle));
		return true;
	}

	/// <inheritdoc />
	public ActivationHandle Register(Action callback, int priority = 0)
	{
		if (callback is null) throw new ArgumentNullException(nameof(callback));

		long id = Interlocked.Increment(ref _nextHandleId);
		if (id > int.MaxValue)
			throw new InvalidOperationException("The activation identifier space is exhausted.");
		var handle = new ActivationHandle((int)id);
		_pendingCommands.Enqueue(ActivationCommand.Register(handle, callback, priority));
		return handle;
	}

	internal bool TryDequeue(out ActivationCommand command) =>
		_pendingCommands.TryDequeue(out command);
}

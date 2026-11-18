using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Bezoro.Chess.UCI.Internal;

internal interface IEventDeliveryGuard
{
	bool IsCurrent { get; }
}

internal sealed class GameEngineEventDispatcher(SynchronizationContext? syncContext)
{
	private readonly AsyncLocal<CommandEvents?> _command = new();
	private readonly Queue<List<Action>> _committed = new();
	private bool _delivering;

	internal CommandEvents BeginCommand(SemaphoreSlim gate)
	{
		lock (_committed)
		{
			if (_committed.Count >= 256)
			{
				gate.Release();
				throw new InvalidOperationException("Gameplay observers have 256 pending command batches. Retry after delivery catches up.");
			}
		}
		return _command.Value = new CommandEvents();
	}

	// A worker may inherit the command's execution context. Once closed it delivers normally.
	internal sealed class CommandEvents
	{
		private bool _closed;
		private List<Action>? _events;

		internal bool TryAdd(Action deliver)
		{
			lock (this)
			{
				if (_closed) return false;
				(_events ??= []).Add(deliver);
				return true;
			}
		}

		internal List<Action>? Close()
		{
			lock (this)
			{
				_closed = true;
				var pending = _events;
				_events = null;
				return pending;
			}
		}
	}

	internal void EndCommand(CommandEvents command, SemaphoreSlim gate)
	{
		var pending = command.Close();
		_command.Value = null;
		if (pending is null)
		{
			gate.Release();
			return;
		}
		// Publish complete batches in commit order. Reentrant commands append behind the batch
		// currently being delivered, and return without waiting on their caller's own observer.
		lock (_committed)
		{
			_committed.Enqueue(pending);
			gate.Release();
			if (_delivering) return;
			_delivering = true;
		}
		while (true)
		{
			lock (_committed)
			{
				if (_committed.Count == 0)
				{
					_delivering = false;
					return;
				}
				pending = _committed.Dequeue();
			}
			foreach (var deliver in pending) deliver();
		}
	}

	private void Dispatch(Action deliver)
	{
		void Send()
		{
			try
			{
				if (syncContext is null) deliver();
				else syncContext.Post(_ => deliver(), null);
			}
			catch (Exception error) { ReportFault(error); }
		}
		if (_command.Value?.TryAdd(Send) != true) Send();
	}

	internal void Raise(Action? handler)
	{
		if (handler is null) return;
		Dispatch(() =>
		{
			foreach (Action observer in handler.GetInvocationList())
			{
				try { observer(); }
				catch (Exception error) { ReportFault(error); }
			}
		});
	}

	internal void Raise<T>(Action<T>? handler, T args)
	{
		if (handler is not null) Dispatch(() => Notify(handler, args));
	}

	internal void RaiseIfCurrent<T, TGuard>(Action<T>? handler, T args, TGuard guard)
		where TGuard : struct, IEventDeliveryGuard
	{
		if (handler is not null) Dispatch(() => { if (guard.IsCurrent) Notify(handler, args); });
	}

	private static void ReportFault(Exception error)
	{
		try { Trace.TraceError("Chess event delivery failed: {0}", error); }
		catch (Exception) { } // A caller-supplied trace listener can also fail.
	}

	internal static void Notify<T>(Action<T> handler, T args)
	{
		foreach (Action<T> observer in handler.GetInvocationList())
		{
			try { observer(args); }
			catch (Exception error) { ReportFault(error); }
		}
	}
}

using System.Threading;

namespace Bezoro.Chess.UCI.Internal;

internal interface IEventDeliveryGuard
{
	bool IsCurrent { get; }
}

internal sealed class GameEngineEventDispatcher(SynchronizationContext? syncContext)
{
	private readonly SynchronizationContext? _syncContext = syncContext;

	public void Raise(Action? handler)
	{
		if (handler is null)
			return;

		if (_syncContext is { })
			_syncContext.Post(_ => handler(), null);
		else
			handler();
	}

	public void Raise<T>(Action<T>? handler, T args)
	{
		if (handler is null)
			return;

		if (_syncContext is { })
			_syncContext.Post(_ => handler(args), null);
		else
			handler(args);
	}

	// A worker can finish before a move/undo but reach the UI queue afterwards. Validate at delivery,
	// rather than relying only on the generation check that accepted its result into the state owner.
	public void RaiseIfCurrent<T, TGuard>(Action<T>? handler, T args, TGuard guard)
		where TGuard : struct, IEventDeliveryGuard
	{
		if (handler is null) return;
		if (_syncContext is { })
			_syncContext.Post(_ => { if (guard.IsCurrent) handler(args); }, null);
		else if (guard.IsCurrent)
			handler(args);
	}
}

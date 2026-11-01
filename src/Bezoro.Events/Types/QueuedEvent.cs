using Bezoro.Events.Services;

namespace Bezoro.Events.Types;

/// <summary>
///     Type-erased queued dispatch. The typed implementation owns its value without a closure or delegate.
/// </summary>
internal abstract class QueuedEvent
{
	public abstract void Dispatch();
}

internal sealed class QueuedEvent<TEvent>(EventBus bus, TEvent value) : QueuedEvent where TEvent : struct
{
	public override void Dispatch() => bus.Publish(value);
}

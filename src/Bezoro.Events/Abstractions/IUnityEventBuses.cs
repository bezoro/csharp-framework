namespace Bezoro.Events.Abstractions;

/// <summary>
///     Aggregates update-phase event buses commonly used in Unity-style game loops.
/// </summary>
public interface IUnityEventBuses : IDisposable
{
    /// <summary>
    ///     Event bus flushed during FixedUpdate.
    /// </summary>
    IEventBus FixedUpdate { get; }

    /// <summary>
    ///     Event bus flushed during LateUpdate.
    /// </summary>
    IEventBus LateUpdate { get; }

    /// <summary>
    ///     Event bus flushed during Update.
    /// </summary>
    IEventBus Update { get; }

    /// <summary>
    ///     Flushes queued events from the FixedUpdate bus.
    /// </summary>
    int FlushFixedUpdate();

    /// <summary>Flushes at most the given number of queued FixedUpdate events.</summary>
    int FlushFixedUpdate(int maxEvents);

    /// <summary>
    ///     Flushes queued events from the LateUpdate bus.
    /// </summary>
    int FlushLateUpdate();

    /// <summary>Flushes at most the given number of queued LateUpdate events.</summary>
    int FlushLateUpdate(int maxEvents);

    /// <summary>
    ///     Flushes queued events from the Update bus.
    /// </summary>
    int FlushUpdate();

    /// <summary>Flushes at most the given number of queued Update events.</summary>
    int FlushUpdate(int maxEvents);
}

namespace Bezoro.StateMachines;

/// <summary>References a signal registered with one construction builder.</summary>
/// <remarks>Resolve the handle through its builder to obtain a compact runtime <see cref="SignalId" />. The default handle is invalid.</remarks>
public readonly struct SignalHandle
{
    internal SignalHandle(StateMachineBuilder owner, int index) { Owner = owner; Index = index; }
    internal StateMachineBuilder? Owner { get; }
    internal int Index { get; }
}

namespace Bezoro.StateMachines;

/// <summary>References a state registered with one construction builder.</summary>
/// <remarks>Resolve the handle through its builder to obtain a compact runtime <see cref="StateId" />. The default handle is invalid.</remarks>
public readonly struct StateHandle
{
    internal StateHandle(StateMachineBuilder owner, int index) { Owner = owner; Index = index; }
    internal StateMachineBuilder? Owner { get; }
    internal int Index { get; }
}

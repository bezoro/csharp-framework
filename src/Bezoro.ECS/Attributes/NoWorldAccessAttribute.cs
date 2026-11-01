namespace Bezoro.ECS.Attributes;

/// <summary>
///     Declares that a system update uses only its own data and context values, without accessing world state.
/// </summary>
/// <remarks>
///     Allows parallel scheduling when no component or resource access metadata is available.
///     Recording deferred commands in the system's own command stream is allowed.
///     The system remains responsible for any shared data outside the world.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true)]
public sealed class NoWorldAccessAttribute : Attribute;

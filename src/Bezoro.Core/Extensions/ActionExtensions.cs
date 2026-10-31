namespace Bezoro.Core.Extensions;

/// <summary>
///     Provides extension methods for safely invoking <see cref="Action" /> delegates.
/// </summary>
public static class ActionExtensions
{
	/// <summary>
	///     Invokes the specified delegate, rejecting null. A callback exception propagates and stops invocation.
	/// </summary>
	/// <param name="handler">The <see cref="Action" /> delegate to invoke.</param>
	/// <exception cref="ArgumentNullException">Thrown if the <paramref name="handler" /> is <c>null</c>.</exception>
	public static void SafeInvoke(this Action handler)
	{
		handler.ThrowIfNull();

		handler();
	}
}

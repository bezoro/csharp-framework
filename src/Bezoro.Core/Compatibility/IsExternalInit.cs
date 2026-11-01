#if NETSTANDARD || NETSTANDARD2_0 || NETSTANDARD2_1
using System.ComponentModel;

// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices
{
	/// <summary>
	///     Enables C# 9 init-only setters on older frameworks.
	/// </summary>
	[EditorBrowsable(EditorBrowsableState.Never)]
	internal static class IsExternalInit { }
}
#else
// Preserve init-setter signatures when a .NET Standard consumer loads the .NET build.
[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(System.Runtime.CompilerServices.IsExternalInit))]
#endif

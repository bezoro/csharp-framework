using System.Diagnostics;
using Bezoro.Core.Abstractions;

namespace Bezoro.Core.Types.Pool;

/// <summary>
///     A disposable handle that automatically returns a pooled object when disposed.
///     Designed for use with <c>using</c> statements to ensure objects are always returned.
/// </summary>
/// <typeparam name="T">The type of the pooled object.</typeparam>
/// <remarks>
///     This readonly struct has copies that share disposal state, ensuring the pooled object is returned only once.
/// </remarks>
[DebuggerDisplay("Value={DebuggerValue}, IsDisposed={IsDisposed}")]
public readonly struct PooledObjectHandle<T> : IDisposable where T : class
{
	private readonly PooledObjectHandleState<T>? _state;

	private readonly T? DebuggerValue => _state?.DebuggerValue;

	/// <summary>
	///     Initializes a new instance of the <see cref="PooledObjectHandle{T}" /> struct.
	/// </summary>
	/// <param name="value">The pooled object.</param>
	/// <param name="pool">The pool that owns the object.</param>
	internal PooledObjectHandle(T value, IPool<T> pool)
	{
		_state = new(value, pool);
	}

	/// <summary>
	///     Gets whether this handle has been disposed.
	/// </summary>
	public bool IsDisposed => _state is null || _state.IsDisposed;

	/// <summary>
	///     Gets the pooled object.
	/// </summary>
	/// <exception cref="ObjectDisposedException">Thrown if accessed after disposal.</exception>
	public T Value => _state?.Value ?? throw new ObjectDisposedException(nameof(PooledObjectHandle<T>));

	/// <summary>
	///     Implicitly converts the handle to the underlying value.
	/// </summary>
	/// <param name="handle">The handle to convert.</param>
	/// <returns>The underlying pooled object.</returns>
	public static implicit operator T(PooledObjectHandle<T> handle) => handle.Value;

	/// <summary>
	///     Returns the object to the pool. Safe to call multiple times.
	/// </summary>
	public void Dispose() => _state?.Dispose();
}

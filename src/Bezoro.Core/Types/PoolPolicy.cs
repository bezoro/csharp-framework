using System.Runtime.CompilerServices;
using Bezoro.Core.Abstractions;
using Bezoro.Core.Extensions;

namespace Bezoro.Core.Types;

/// <summary>
///     A flexible, delegate-based pool policy for common pooling scenarios.
/// </summary>
/// <typeparam name="T">The type of objects managed by the policy.</typeparam>
/// <remarks>
///     Creates a policy with the specified factory and optional lifecycle delegates.
/// </remarks>
/// <param name="factory">Factory to create new instances.</param>
/// <param name="reset">Optional reset delegate returning <c>true</c> if the object is reusable.</param>
/// <param name="validate">Optional validation delegate returning <c>true</c> if the object is valid.</param>
/// <param name="onDiscard">Optional callback invoked when an object is discarded.</param>
/// <exception cref="ArgumentNullException">Thrown when <paramref name="factory" /> is <c>null</c>.</exception>
public sealed class PoolPolicy<T>(
	Func<T>        factory,
	Func<T, bool>? reset     = null,
	Func<T, bool>? validate  = null,
	Action<T>?     onDiscard = null
) : IPoolPolicy<T> where T : class
{
	private readonly Action<T>?     _onDiscard = onDiscard;
	private readonly Func<T, bool>? _reset     = reset;
	private readonly Func<T, bool>? _validate  = validate;
	private readonly Func<T>        _factory   = factory.ThrowIfNull();

	/// <inheritdoc />
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public T Create() => _factory();

	/// <inheritdoc />
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool Reset(T item)
	{
		if (item is IPooledObject pooled)
			return pooled.OnReturn();

		return _reset?.Invoke(item) ?? true;
	}

	/// <inheritdoc />
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool Validate(T item) => _validate?.Invoke(item) ?? true;

	/// <inheritdoc />
	public void OnDiscard(T item)
	{
		try { _onDiscard?.Invoke(item); }
		catch (Exception callbackFailure)
		{
			try { if (item is IDisposable failedItem) failedItem.Dispose(); }
			catch (Exception disposalFailure)
			{
				throw new AggregateException("The discard callback and object disposal both failed.", callbackFailure, disposalFailure);
			}
			throw;
		}
		if (item is IDisposable disposable) disposable.Dispose();
	}
}

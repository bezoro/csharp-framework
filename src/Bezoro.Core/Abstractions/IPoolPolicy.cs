namespace Bezoro.Core.Abstractions;

/// <summary>
///     Defines the lifecycle and validation policy for pooled objects.
/// </summary>
/// <typeparam name="T">The type of objects managed by the pool.</typeparam>
/// <remarks>Callbacks execute synchronously on the operation's thread and may run concurrently for distinct objects.</remarks>
public interface IPoolPolicy<T> where T : class
{
	/// <summary>
	///     Resets an object to a clean state before returning it to the pool.
	/// </summary>
	/// <param name="item">The object to reset.</param>
	/// <returns><c>true</c> if the object was successfully reset and should be pooled; <c>false</c> to discard.</returns>
	bool Reset(T item);

	/// <summary>
	///     Validates an object before it is rented out.
	/// </summary>
	/// <param name="item">The object to validate.</param>
	/// <returns><c>true</c> if the object is valid for use; <c>false</c> to discard and create a new one.</returns>
	bool Validate(T item);

	/// <summary>
	///     Creates a new instance of the pooled object type.
	/// </summary>
	/// <returns>A new instance of <typeparamref name="T" />.</returns>
	T Create();

	/// <summary>
	///     Called when an object is being discarded and will not be returned to the pool.
	/// </summary>
	/// <param name="item">The object being discarded.</param>
	/// <remarks>
	///     This is the final release operation for validation/reset rejection, trimming, clearing, and disposal.
	///     The policy owns native-resource cleanup and any managed disposal. The pool attempts this callback
	///     once per owned release and releases capacity even when it throws.
	/// </remarks>
	void OnDiscard(T item);
}

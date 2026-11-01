using Bezoro.ECS.Types;

namespace Bezoro.ECS.Internal;

internal sealed class WorldResourceStore
{
	private readonly Dictionary<Type, IResourceBox> _resources = [];
	private readonly object _gate = new();
	private bool _invokingUserCode;
	private bool _disposed;

	public int Count { get { lock (_gate) return _resources.Count; } }
	public bool IsInvokingUserCode { get { lock (_gate) return _invokingUserCode; } }

	public void Clear()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_resources.Count == 0) return;
			EnsureCanMutate();
			ClearCore();
		}
	}

	private void ClearCore()
	{
		// Detach ownership before invoking user code, including reentrant disposal.
		IResourceBox[] boxes = [.. _resources.Values];
		_resources.Clear();
		List<Exception>? errors = null;
		foreach (var box in boxes)
		{
			try { DisposeValue(box); }
			catch (Exception error) { (errors ??= []).Add(error); }
		}
		if (errors is not null)
			throw new AggregateException("One or more resources failed to dispose.", errors);
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed) return;
			EnsureCanMutate();
			_disposed = true;
			ClearCore();
		}
	}

	public ref T Get<T>() where T : notnull
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!_resources.TryGetValue(typeof(T), out var boxed))
				throw new KeyNotFoundException($"Resource of type {typeof(T).Name} was not found.");
			return ref GetTypedBox<T>(boxed).Value;
		}
	}

	public bool Has<T>() where T : notnull
	{
		lock (_gate) { ThrowIfDisposed(); return _resources.ContainsKey(typeof(T)); }
	}

	public bool TryRead<T>(out T resource) where T : notnull
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_resources.TryGetValue(typeof(T), out var boxed))
			{
				resource = GetTypedBox<T>(boxed).Value;
				return true;
			}
			resource = default!;
			return false;
		}
	}

	public ref T GetOrCreate<T>() where T : notnull, new()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_resources.TryGetValue(typeof(T), out var boxed)) return ref GetTypedBox<T>(boxed).Value;
			EnsureCanMutate();
			T value;
			_invokingUserCode = true;
			try { value = new T(); }
			finally { _invokingUserCode = false; }
			var created = new ResourceBox<T>(value);
			_resources.Add(typeof(T), created);
			return ref created.Value;
		}
	}

	public ref T GetOrCreate<T>(Func<T> factory) where T : notnull
	{
		if (factory is null) throw new ArgumentNullException(nameof(factory));
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_resources.TryGetValue(typeof(T), out var boxed)) return ref GetTypedBox<T>(boxed).Value;
			EnsureCanMutate();
			T value;
			_invokingUserCode = true;
			try { value = factory(); }
			finally { _invokingUserCode = false; }
			if (value is null) throw new InvalidOperationException("Resource factory returned null.");
			var created = new ResourceBox<T>(value);
			_resources.Add(typeof(T), created);
			return ref created.Value;
		}
	}

	public void Set<T>(T resource) where T : notnull
	{
		if (resource is null) throw new ArgumentNullException(nameof(resource));
		lock (_gate) Replace(typeof(T), new ResourceBox<T>(resource));
	}

	public bool Remove<T>() where T : notnull
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!_resources.TryGetValue(typeof(T), out var existing)) return false;
			EnsureCanMutate();
			_resources.Remove(typeof(T));
			DisposeValue(existing);
			return true;
		}
	}

	public SnapshotResourceRecord[] CaptureSnapshotRecords()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			var resources = new SnapshotResourceRecord[_resources.Count];
			int index = 0;
			foreach (var box in _resources.Values) resources[index++] = new(box.ResourceType, box.BoxedValue);
			return resources;
		}
	}

	public void SetBoxed(Type resourceType, object value)
	{
		if (resourceType is null) throw new ArgumentNullException(nameof(resourceType));
		if (value is null) throw new ArgumentNullException(nameof(value));
		if (!resourceType.IsInstanceOfType(value))
			throw new ArgumentException(
				$"Resource value type '{value.GetType().FullName}' is not assignable to '{resourceType.FullName}'.", nameof(value));
		lock (_gate) Replace(resourceType, new SnapshotResourceBox(resourceType, value));
	}

	private ResourceBox<T> GetTypedBox<T>(IResourceBox boxed) where T : notnull
	{
		if (boxed is ResourceBox<T> typed) return typed;
		// Materialize once under the store lock, including parallel read-only access after restore.
		var materialized = new ResourceBox<T>((T)boxed.BoxedValue);
		_resources[typeof(T)] = materialized;
		return materialized;
	}

	private void Replace(Type resourceType, IResourceBox replacement)
	{
		ThrowIfDisposed();
		EnsureCanMutate();
		if (_resources.TryGetValue(resourceType, out var existing))
		{
			if (!resourceType.IsValueType && ReferenceEquals(existing.BoxedValue, replacement.BoxedValue)) return;
			_resources.Remove(resourceType);
			DisposeValue(existing);
		}
		_resources.Add(resourceType, replacement);
	}

	private void DisposeValue(IResourceBox box)
	{
		_invokingUserCode = true;
		try { box.DisposeValue(); }
		finally { _invokingUserCode = false; }
	}

	private void EnsureCanMutate()
	{
		if (_invokingUserCode)
			throw new InvalidOperationException("Resource factories and disposal callbacks cannot mutate the resource store.");
	}

	private void ThrowIfDisposed()
	{
		if (_disposed) throw new ObjectDisposedException(nameof(WorldResourceStore));
	}

	private sealed class SnapshotResourceBox(Type resourceType, object value) : IResourceBox
	{
		public object BoxedValue { get; } = value;
		public Type ResourceType { get; } = resourceType;
		public void DisposeValue() { if (BoxedValue is IDisposable disposable) disposable.Dispose(); }
	}
}

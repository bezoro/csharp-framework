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
		IResourceBox[] boxes;
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_resources.Count == 0) return;
			EnsureCanMutate();
			boxes = DetachAll();
		}
		DisposeDetached(boxes);
	}

	private IResourceBox[] DetachAll()
	{
		// Claim the mutation and detach ownership before releasing the gate for user code.
		IResourceBox[] boxes = [.. _resources.Values];
		_resources.Clear();
		_invokingUserCode = true;
		return boxes;
	}

	private void DisposeDetached(IResourceBox[] boxes)
	{
		try
		{
			List<Exception>? errors = null;
			foreach (var box in boxes)
			{
				try { box.DisposeValue(); }
				catch (Exception error) { (errors ??= []).Add(error); }
			}
			if (errors is not null)
				throw new AggregateException("One or more resources failed to dispose.", errors);
		}
		finally { EndUserCode(); }
	}

	public void Dispose()
	{
		IResourceBox[] boxes;
		lock (_gate)
		{
			if (_disposed) return;
			EnsureCanMutate();
			_disposed = true;
			boxes = DetachAll();
		}
		DisposeDetached(boxes);
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
			_invokingUserCode = true;
		}
		try { return ref PublishCreated(new T()); }
		finally { EndUserCode(); }
	}

	public ref T GetOrCreate<T>(Func<T> factory) where T : notnull
	{
		if (factory is null) throw new ArgumentNullException(nameof(factory));
		lock (_gate)
		{
			ThrowIfDisposed();
			if (_resources.TryGetValue(typeof(T), out var boxed)) return ref GetTypedBox<T>(boxed).Value;
			EnsureCanMutate();
			_invokingUserCode = true;
		}
		try { return ref PublishCreated(factory()); }
		finally { EndUserCode(); }
	}

	private ref T PublishCreated<T>(T value) where T : notnull
	{
		if (value is null) throw new InvalidOperationException("Resource factory returned null.");
		var created = new ResourceBox<T>(value);
		lock (_gate)
		{
			_resources.Add(typeof(T), created);
			return ref created.Value;
		}
	}

	public void Set<T>(T resource) where T : notnull
	{
		if (resource is null) throw new ArgumentNullException(nameof(resource));
		Replace(typeof(T), new ResourceBox<T>(resource));
	}

	public bool Remove<T>() where T : notnull
	{
		IResourceBox existing;
		lock (_gate)
		{
			ThrowIfDisposed();
			if (!_resources.TryGetValue(typeof(T), out existing!)) return false;
			EnsureCanMutate();
			_resources.Remove(typeof(T));
			_invokingUserCode = true;
		}
		try { existing.DisposeValue(); return true; }
		finally { EndUserCode(); }
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
		Replace(resourceType, new SnapshotResourceBox(resourceType, value));
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
		IResourceBox existing;
		lock (_gate)
		{
			ThrowIfDisposed();
			EnsureCanMutate();
			if (!_resources.TryGetValue(resourceType, out existing!))
			{
				_resources.Add(resourceType, replacement);
				return;
			}
			if (!resourceType.IsValueType && ReferenceEquals(existing.BoxedValue, replacement.BoxedValue)) return;
			_resources.Remove(resourceType);
			_invokingUserCode = true;
		}
		try
		{
			existing.DisposeValue();
			lock (_gate) _resources.Add(resourceType, replacement);
		}
		finally { EndUserCode(); }
	}

	private void EndUserCode()
	{
		lock (_gate) _invokingUserCode = false;
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

using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace Bezoro.Core.Types;

/// <summary>Creates one default instance per type and supports owned, nested overrides.</summary>
/// <remarks>
/// Lifecycle reservations are serialized; factories and disposal callbacks run outside the monitor.
/// Reading an existing instance does not lock. Concurrent default readers join one pending creation.
/// Overlapping lifecycle changes fail before mutation. Factories cannot recursively request their own
/// pending instance; factories with circular dependencies across threads are unsupported.
/// Finish using borrowed instances before resetting, recreating, or disposing their owning override.
/// </remarks>
/// <typeparam name="T">The singleton's reference type.</typeparam>
public abstract class Singleton<T> where T : class
{
	private static readonly object Sync = new();
	private static Func<T> _factory = DefaultFactory;
	private static T? _instance;
	private static T? _initializedOverride;
	private static T? _overrideInstance;
	private static OverrideScope? _scope;
	private static ExceptionDispatchInfo? _creationFailure;
	private static int _initializingThread;
	private static bool _releasing;

	/// <summary>Rejects direct construction outside the owning thread's factory call.</summary>
	protected Singleton()
	{
		if (Volatile.Read(ref _initializingThread) != Environment.CurrentManagedThreadId)
			throw new InvalidOperationException($"Direct construction of {typeof(T).FullName} is not allowed. Use Instance or a configured factory.");
	}

	/// <summary>Eagerly creates an owned override. Fails if any instance already exists.</summary>
	public static void Initialize(Func<T> factory)
	{
		if (factory is null) throw new ArgumentNullException(nameof(factory));
		lock (Sync)
		{
			RequireLifecycleChange();
			if (_instance is not null || _overrideInstance is not null)
				throw new InvalidOperationException("The singleton has already been created or overridden.");
			BeginCreation();
		}
		var current = CreateReserved(factory);
		lock (Sync)
		{
			_initializedOverride = current;
			Volatile.Write(ref _overrideInstance, current);
			CompleteCreation();
		}
	}

	/// <summary>Gets whether an initialized or scoped override is active.</summary>
	public static bool IsOverridden => Volatile.Read(ref _overrideInstance) is not null;

	/// <summary>Gets whether a default or override instance exists, without creating one.</summary>
	public static bool IsValueCreated => TryGet(out _);

	/// <summary>Gets the current override or creates the default instance once.</summary>
	/// <remarks>A default-factory failure is retained until reset or recreation.</remarks>
	public static T Instance
	{
		get
		{
			if (TryGet(out var current)) return current;
			Func<T> creator;
			lock (Sync)
			{
				while (true)
				{
					if (TryGet(out current)) return current;
					if (_initializingThread == 0) break;
					if (_initializingThread == Environment.CurrentManagedThreadId)
						throw new InvalidOperationException("A singleton factory cannot request its own pending instance.");
					Monitor.Wait(Sync);
				}
				RequireLifecycleChange();
				_creationFailure?.Throw();
				creator = _factory;
				BeginCreation();
			}
			current = CreateReserved(creator, cacheFailure: true);
			lock (Sync)
			{
				Volatile.Write(ref _instance, current);
				CompleteCreation();
				return current;
			}
		}
	}

	/// <summary>Reads an existing instance without invoking the factory.</summary>
	public static bool TryGet([NotNullWhen(true)] out T? instance)
	{
		instance = Volatile.Read(ref _overrideInstance) ?? Volatile.Read(ref _instance);
		return instance is not null;
	}

	/// <summary>Creates an owned override that is disposed when its scope closes.</summary>
	/// <remarks>
	/// Close scopes in reverse creation order. Out-of-order disposal throws without changing ownership.
	/// Reset and recreation invalidate outstanding scopes. A factory must return a distinct instance.
	/// </remarks>
	public static IDisposable Override(Func<T> factory)
	{
		if (factory is null) throw new ArgumentNullException(nameof(factory));
		lock (Sync) { RequireLifecycleChange(); BeginCreation(); }
		var current = CreateReserved(factory);
		lock (Sync)
		{
			try
			{
				if (ReferenceEquals(current, _instance) || ReferenceEquals(current, _initializedOverride))
					throw new InvalidOperationException("The override factory returned an instance already owned by this singleton.");
				for (var scope = _scope; scope is not null; scope = scope.Parent)
					if (ReferenceEquals(current, scope.Value))
						throw new InvalidOperationException("The override factory returned an instance already owned by this singleton.");
				_scope = new OverrideScope(current, _scope);
				Volatile.Write(ref _overrideInstance, current);
				return _scope;
			}
			finally { CompleteCreation(); }
		}
	}

	/// <summary>Sets the factory for future default creation, preserving existing instances.</summary>
	public static void ConfigureFactory(Func<T> factory) => ConfigureFactoryCore(factory, false);

	/// <summary>Disposes all owned instances and sets the factory for the next default read.</summary>
	public static void ConfigureFactoryAndRecreate(Func<T> factory) => ConfigureFactoryCore(factory, true);

	/// <summary>Sets a factory, optionally releasing existing instances first.</summary>
	[Obsolete("Use ConfigureFactory(Func<T>) or ConfigureFactoryAndRecreate(Func<T>) instead.")]
	public static void ConfigureFactory(Func<T> factory, bool recreateIfInitialized) => ConfigureFactoryCore(factory, recreateIfInitialized);

	private static void ConfigureFactoryCore(Func<T> factory, bool recreate)
	{
		if (factory is null) throw new ArgumentNullException(nameof(factory));
		ReleasedInstances released;
		lock (Sync)
		{
			RequireLifecycleChange();
			if (!recreate) { _factory = factory; return; }
			released = DetachInstances();
			_factory = factory;
		}
		ReleaseDetached(released, dispose: true);
	}

	/// <summary>Forgets all instances without disposing them and restores the default factory.</summary>
	public static void Reset() => ResetCore(false);

	/// <summary>Disposes all owned instances and restores the default factory. Disposal exceptions are suppressed.</summary>
	public static void ResetAndDispose() => ResetCore(true);

	/// <summary>Resets the factory and optionally disposes owned instances.</summary>
	[Obsolete("Use Reset() or ResetAndDispose() instead.")]
	public static void Reset(bool disposeInstances) => ResetCore(disposeInstances);

	private static void ResetCore(bool dispose)
	{
		ReleasedInstances released;
		lock (Sync)
		{
			RequireLifecycleChange();
			released = DetachInstances();
			_factory = DefaultFactory;
		}
		ReleaseDetached(released, dispose);
	}

	private static void RequireLifecycleChange()
	{
		if (_initializingThread != 0 || _releasing)
			throw new InvalidOperationException("A singleton lifecycle operation is already in progress.");
	}

	// Callers hold Sync for reservation/publication, never while invoking user callbacks.
	private static void BeginCreation() => Volatile.Write(ref _initializingThread, Environment.CurrentManagedThreadId);
	private static void CompleteCreation()
	{
		Volatile.Write(ref _initializingThread, 0);
		Monitor.PulseAll(Sync);
	}

	private static T CreateReserved(Func<T> creator, bool cacheFailure = false)
	{
		try
		{
			try
			{
				return creator() ?? throw new TypeInitializationException(typeof(T).FullName,
					new InvalidOperationException("Singleton factory returned null."));
			}
			catch (MissingMethodException error)
			{
				throw new TypeInitializationException(typeof(T).FullName,
					new InvalidOperationException("Provide a parameterless constructor or configure a singleton factory.", error));
			}
		}
		catch (Exception error)
		{
			lock (Sync)
			{
				try { if (cacheFailure) _creationFailure = ExceptionDispatchInfo.Capture(error); }
				finally { CompleteCreation(); }
			}
			throw;
		}
	}

	private static T DefaultFactory()
	{
		var t = typeof(T);

		if (t.IsInterface)
			throw new TypeInitializationException(
				t.FullName ?? t.Name,
				new InvalidOperationException(
					$"Cannot create a singleton for interface type {t.FullName}. Configure a factory."
				)
			);

		if (t.IsAbstract)
			throw new TypeInitializationException(
				t.FullName ?? t.Name,
				new InvalidOperationException(
					$"Cannot create a singleton for abstract type {t.FullName}. Configure a factory."
				)
			);

		if (t.ContainsGenericParameters)
			throw new TypeInitializationException(
				t.FullName ?? t.Name,
				new InvalidOperationException($"Cannot create a singleton for open generic type {t.FullName}.")
			);

		object? instance = Activator.CreateInstance(t, true);
		if (instance is null)
			throw new TypeInitializationException(
				t.FullName ?? t.Name,
				new InvalidOperationException($"Could not create an instance of type {t.FullName}.")
			);

		return (T)instance;
	}


	private readonly record struct ReleasedInstances(T? Current, T? Initialized, OverrideScope? Scope);

	// Detach ownership and invalidate every scope under Sync before invoking any disposal callback.
	private static ReleasedInstances DetachInstances()
	{
		var released = new ReleasedInstances(_instance, _initializedOverride, _scope);
		Volatile.Write(ref _instance, null);
		Volatile.Write(ref _overrideInstance, null);
		_initializedOverride = null;
		_scope = null;
		_creationFailure = null;
		_releasing = true;
		for (var scope = released.Scope; scope is not null; scope = scope.Parent) scope.MarkInvalid();
		return released;
	}

	private static void ReleaseDetached(ReleasedInstances released, bool dispose)
	{
		try
		{
			var scope = released.Scope;
			while (scope is not null)
			{
				var parent = scope.Parent;
				var value = scope.Value;
				scope.ClearReleasedReferences();
				if (dispose) DisposeInstance(value);
				scope = parent;
			}
			if (dispose)
			{
				DisposeInstance(released.Initialized);
				DisposeInstance(released.Current);
			}
		}
		finally { CompleteRelease(); }
	}

	private static void CompleteRelease()
	{
		lock (Sync) { _releasing = false; Monitor.PulseAll(Sync); }
	}

	private static void DisposeInstance(T? instance)
	{
		if (instance is not IDisposable disposable) return;
		try { disposable.Dispose(); }
		catch { /* Disposal remains best-effort, including during scope closure. */ }
	}

	private sealed class OverrideScope(T value, OverrideScope? parent) : IDisposable
	{
		private bool _invalidated;
		internal T? Value { get; private set; } = value;
		internal OverrideScope? Parent { get; private set; } = parent;
		internal void MarkInvalid() => _invalidated = true;
		internal void ClearReleasedReferences() { Value = null; Parent = null; }
		internal void Invalidate() { MarkInvalid(); ClearReleasedReferences(); }

		public void Dispose()
		{
			T? released;
			lock (Sync)
			{
				if (_invalidated) return;
				RequireLifecycleChange();
				if (!ReferenceEquals(_scope, this))
					throw new InvalidOperationException("Close singleton override scopes in reverse creation order.");
				released = Value;
				_scope = Parent;
				Volatile.Write(ref _overrideInstance, Parent?.Value ?? _initializedOverride);
				Invalidate();
				_releasing = true;
			}
			try { DisposeInstance(released); }
			finally { CompleteRelease(); }
		}
	}
}

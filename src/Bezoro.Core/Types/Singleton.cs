using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace Bezoro.Core.Types;

/// <summary>Creates one default instance per type and supports owned, nested overrides.</summary>
/// <remarks>
/// Creation, configuration, and scope changes are serialized. Reading an existing instance does not lock.
/// The application must finish using borrowed instances before resetting, recreating, or disposing an override.
/// Factories run on the calling thread and cannot recursively change this singleton's lifecycle.
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
			_initializedOverride = CreateWithGuard(factory);
			Volatile.Write(ref _overrideInstance, _initializedOverride);
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
			lock (Sync)
			{
				if (TryGet(out current)) return current;
				RequireLifecycleChange();
				_creationFailure?.Throw();
				try
				{
					current = CreateWithGuard(_factory);
					Volatile.Write(ref _instance, current);
					return current;
				}
				catch (Exception error)
				{
					_creationFailure = ExceptionDispatchInfo.Capture(error);
					throw;
				}
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
		lock (Sync)
		{
			RequireLifecycleChange();
			var current = CreateWithGuard(factory);
			if (ReferenceEquals(current, _instance) || ReferenceEquals(current, _initializedOverride))
				throw new InvalidOperationException("The override factory returned an instance already owned by this singleton.");
			for (var scope = _scope; scope is not null; scope = scope.Parent)
				if (ReferenceEquals(current, scope.Value))
					throw new InvalidOperationException("The override factory returned an instance already owned by this singleton.");
			_scope = new OverrideScope(current, _scope);
			Volatile.Write(ref _overrideInstance, current);
			return _scope;
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
		lock (Sync)
		{
			RequireLifecycleChange();
			if (recreate) ReleaseInstances(dispose: true);
			_factory = factory;
		}
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
		lock (Sync)
		{
			RequireLifecycleChange();
			ReleaseInstances(dispose);
			_factory = DefaultFactory;
		}
	}

	private static void RequireLifecycleChange()
	{
		if (_initializingThread != 0 || _releasing)
			throw new InvalidOperationException("A singleton factory or disposal callback cannot change its lifecycle recursively.");
	}

	private static T CreateWithGuard(Func<T> creator)
	{
		Volatile.Write(ref _initializingThread, Environment.CurrentManagedThreadId);
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
		finally { Volatile.Write(ref _initializingThread, 0); }
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


	// All callers hold Sync. Detach before callbacks so scopes cannot restore released objects.
	private static void ReleaseInstances(bool dispose)
	{
		var current = _instance;
		var initialized = _initializedOverride;
		var scope = _scope;
		Volatile.Write(ref _instance, null);
		Volatile.Write(ref _overrideInstance, null);
		_initializedOverride = null;
		_scope = null;
		_creationFailure = null;
		_releasing = true;
		try
		{
			while (scope is not null)
			{
				var parent = scope.Parent;
				var value = scope.Value;
				scope.Invalidate();
				if (dispose) DisposeInstance(value);
				scope = parent;
			}
			if (dispose)
			{
				DisposeInstance(initialized);
				DisposeInstance(current);
			}
		}
		finally { _releasing = false; }
	}

	private static void DisposeInstance(T? instance)
	{
		if (instance is not IDisposable disposable) return;
		try { disposable.Dispose(); }
		catch { /* Disposal remains best-effort, including during scope closure. */ }
	}

	private sealed class OverrideScope(T value, OverrideScope? parent) : IDisposable
	{
		internal T? Value { get; private set; } = value;
		internal OverrideScope? Parent { get; private set; } = parent;

		internal void Invalidate() { Value = null; Parent = null; }

		public void Dispose()
		{
			lock (Sync)
			{
				if (Value is null) return;
				RequireLifecycleChange();
				if (!ReferenceEquals(_scope, this))
					throw new InvalidOperationException("Close singleton override scopes in reverse creation order.");
				var released = Value;
				_scope = Parent;
				Volatile.Write(ref _overrideInstance, Parent?.Value ?? _initializedOverride);
				Invalidate();
				_releasing = true;
				try { DisposeInstance(released); }
				finally { _releasing = false; }
			}
		}
	}
}

using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Internal;

internal sealed class WorldSystemRuntime
{
	private readonly SystemManager _systemManager;
	private const int Updating = 1;
	private const int Registering = 2;
	private const int ShuttingDown = 3;
	// Only registration may nest, and only on its current owning thread. Shutdown never releases its claim.
	private int _operation;
	private int _registrationDepth;
	private int _registrationThreadId;

	public WorldSystemRuntime(int maxDegreeOfParallelism)
	{
		_systemManager = new(maxDegreeOfParallelism);
	}

	public int PlanBuildCount => _systemManager.PlanBuildCount;
	public bool IsUpdating => Volatile.Read(ref _operation) == Updating;
	public bool IsRegistering => Volatile.Read(ref _operation) == Registering;

	public void AddSystem(World world, ISystem system, Stage? stage)
	{
		int threadId = Environment.CurrentManagedThreadId;
		int previous = Interlocked.CompareExchange(ref _operation, Registering, 0);
		if (previous != 0 && (previous != Registering || Volatile.Read(ref _registrationThreadId) != threadId))
			throw new InvalidOperationException("System registration cannot overlap another registration, a world update, or disposal.");
		Volatile.Write(ref _registrationThreadId, threadId);
		_registrationDepth++;
		try { _systemManager.RegisterSystem(world, system, stage); }
		finally
		{
			if (--_registrationDepth == 0)
			{
				Volatile.Write(ref _registrationThreadId, 0);
				Volatile.Write(ref _operation, 0);
			}
		}
	}

	public void ClearSystemSetRunCondition(Type setType) =>
		_systemManager.ClearSystemSetRunCondition(setType);

	public ScheduleDiagnostics GetDiagnostics() => _systemManager.GetDiagnostics();

	public bool IsSystemSetEnabled(Type setType) => _systemManager.IsSystemSetEnabled(setType);

	public void RunPhase(World world, SystemLoopPhase loopPhase, float deltaTime)
	{
		if (Interlocked.CompareExchange(ref _operation, Updating, 0) != 0)
			throw new InvalidOperationException("World updates cannot overlap another update, system registration or disposal.");

		try
		{
			_systemManager.UpdatePhase(world, loopPhase, deltaTime);
		}
		finally
		{
			Volatile.Write(ref _operation, 0);
		}
	}

	public bool BeginShutdown()
	{
		int previous = Interlocked.CompareExchange(ref _operation, ShuttingDown, 0);
		if (previous == ShuttingDown) return false;
		if (previous != 0)
			throw new InvalidOperationException("Dispose cannot run while a system update or system registration is active.");
		return true;
	}

	public void SetSystemSetEnabled(Type setType, bool enabled) =>
		_systemManager.SetSystemSetEnabled(setType, enabled);

	public void SetSystemSetRunCondition(Type setType, ISystemRunCondition runCondition) =>
		_systemManager.SetSystemSetRunCondition(setType, runCondition);

	public void Shutdown(World world) => _systemManager.Shutdown(world);
}

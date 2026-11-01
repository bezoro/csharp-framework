using System.Runtime.ExceptionServices;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Internal;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Services;

/// <summary>
///     Manages system lifecycle and staged execution.
/// </summary>
internal sealed class SystemManager
{
	private const int FIRST_RESOURCE_ACCESS_ID = -1;

	private readonly Dictionary<SystemLoopPhase, Dictionary<Stage, List<SystemState[]>>> _phaseStagePlans = new();
	private readonly Dictionary<Type, bool> _setEnabledByType = [];
	private readonly Dictionary<Type, int> _resourceTypeToAccessId = [];
	private readonly Dictionary<Type, ISystemRunCondition> _setRunConditionsByType = [];
	private readonly List<SystemState> _systems = [];
	private readonly SystemBatchExecutor _batchExecutor;
	private readonly SystemExecutionPlanBuilder _planBuilder = new();
	private readonly SystemRegistrationInspector _registrationInspector;
	private          bool _isPlanDirty = true;
	private          int _nextResourceAccessId = FIRST_RESOURCE_ACCESS_ID;

	public SystemManager() : this(Environment.ProcessorCount) { }

	public SystemManager(int maxDegreeOfParallelism)
	{
		if (maxDegreeOfParallelism <= 0)
			throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), "Parallelism must be positive.");

		_registrationInspector = new(new());
		_batchExecutor         = new(maxDegreeOfParallelism);
	}

	internal int PlanBuildCount { get; private set; }

	public bool IsSystemSetEnabled(Type setType)
	{
		if (setType is null)
			throw new ArgumentNullException(nameof(setType));

		return !_setEnabledByType.TryGetValue(setType, out bool enabled) || enabled;
	}

	public ScheduleDiagnostics GetDiagnostics()
	{
		if (_isPlanDirty && _systems.Count > 0)
			RebuildExecutionPlan();

		var phases = new List<SchedulePhaseDiagnostics>();
		foreach (var loopPhase in StageOrder.LoopPhases)
		{
			if (!_phaseStagePlans.TryGetValue(loopPhase, out var stagePlans))
				continue;

			var stages = new List<ScheduleStageDiagnostics>();
			foreach (var stage in StageOrder.Stages)
			{
				if (!stagePlans.TryGetValue(stage, out var stageBatches) || stageBatches.Count == 0)
					continue;

				var batches = new ScheduleBatchDiagnostics[stageBatches.Count];
				for (var batchIndex = 0; batchIndex < stageBatches.Count; batchIndex++)
					batches[batchIndex] = CreateBatchDiagnostics(batchIndex, stageBatches[batchIndex]);

				stages.Add(new(stage, batches));
			}

			if (stages.Count > 0)
				phases.Add(new(loopPhase, [.. stages]));
		}

		return new(_systems.Count, PlanBuildCount, [.. phases]);
	}

	public void ClearSystemSetRunCondition(Type setType)
	{
		if (setType is null)
			throw new ArgumentNullException(nameof(setType));

		_setRunConditionsByType.Remove(setType);
	}

	public void RegisterSystem(World world, ISystem system, Stage? explicitStage = null)
	{
		if (world is null) throw new ArgumentNullException(nameof(world));
		if (system is null) throw new ArgumentNullException(nameof(system));
		Stage stage = explicitStage ?? system.Stage;
		if (stage is not (Stage.Input or Stage.PreTick or Stage.Tick or Stage.PostTick or Stage.Render))
			throw new ArgumentOutOfRangeException(nameof(explicitStage), "Unknown system stage.");
		if (system.LoopPhase is not (SystemLoopPhase.Tick or SystemLoopPhase.FixedTick or SystemLoopPhase.LateTick))
			throw new ArgumentOutOfRangeException(nameof(system), "Unknown system loop phase.");

		for (var i = 0; i < _systems.Count; i++)
		{
			if (ReferenceEquals(_systems[i].System, system))
				throw new InvalidOperationException("System is already registered.");
		}

		var state = _registrationInspector.Inspect(
			world,
			system,
			stage,
			GetOrCreateResourceAccessTypeId,
			EnsureSystemSetKnown
		);

		_systems.Add(state);
		_isPlanDirty = true;
		try { system.OnCreate(world); }
		catch
		{
			_systems.Remove(state);
			_isPlanDirty = true;
			throw;
		}
	}

	public void SetSystemSetEnabled(Type setType, bool enabled)
	{
		if (setType is null)
			throw new ArgumentNullException(nameof(setType));

		_setEnabledByType[setType] = enabled;
	}

	public void SetSystemSetRunCondition(Type setType, ISystemRunCondition runCondition)
	{
		if (setType is null)
			throw new ArgumentNullException(nameof(setType));

		if (runCondition is null)
			throw new ArgumentNullException(nameof(runCondition));

		_setRunConditionsByType[setType] = runCondition;
	}

	public void Shutdown(World world)
	{
		if (world is null) throw new ArgumentNullException(nameof(world));

		List<Exception>? exceptions = null;
		try
		{
			for (int i = _systems.Count - 1; i >= 0; i--)
			{
				try
				{
					_systems[i].System.OnDestroy(world);
				}
				catch (Exception ex)
				{
					(exceptions ??= []).Add(ex);
				}
			}
		}
		finally
		{
			_systems.Clear();
			_phaseStagePlans.Clear();
			_resourceTypeToAccessId.Clear();
			_setEnabledByType.Clear();
			_setRunConditionsByType.Clear();
			_nextResourceAccessId = FIRST_RESOURCE_ACCESS_ID;
			_isPlanDirty          = true;
		}

		if (exceptions is null)
			return;

		if (exceptions.Count == 1)
			ExceptionDispatchInfo.Capture(exceptions[0]).Throw();

		throw new AggregateException("One or more systems failed during shutdown.", exceptions);
	}

	public void UpdatePhase(World world, SystemLoopPhase loopPhase, float deltaTime)
	{
		if (world is null) throw new ArgumentNullException(nameof(world));
		if (loopPhase is not (SystemLoopPhase.Tick or SystemLoopPhase.FixedTick or SystemLoopPhase.LateTick))
			throw new ArgumentOutOfRangeException(nameof(loopPhase));
		if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime) || deltaTime < 0f)
			throw new ArgumentOutOfRangeException(nameof(deltaTime), "Elapsed time must be finite and non-negative.");

		if (_systems.Count == 0)
			return;

		if (_isPlanDirty)
			RebuildExecutionPlan();

		if (_phaseStagePlans.TryGetValue(loopPhase, out var stagePlans))
			_batchExecutor.UpdatePhase(
				world,
				loopPhase,
				stagePlans,
				_setEnabledByType,
				_setRunConditionsByType,
				deltaTime
			);
	}

	private static ScheduleBatchDiagnostics CreateBatchDiagnostics(int batchIndex, SystemState[] batchStates)
	{
		var systemTypes             = new Type[batchStates.Length];
		var containsExclusiveSystem = false;
		for (var stateIndex = 0; stateIndex < batchStates.Length; stateIndex++)
		{
			var state = batchStates[stateIndex];
			systemTypes[stateIndex] = state.System.GetType();
			if (state.IsExclusive)
				containsExclusiveSystem = true;
		}

		return new(batchIndex, systemTypes, containsExclusiveSystem);
	}

	private int GetOrCreateResourceAccessTypeId(Type resourceType)
	{
		if (resourceType is null)
			throw new ArgumentNullException(nameof(resourceType));

		if (_resourceTypeToAccessId.TryGetValue(resourceType, out int existing))
			return existing;

		int id = _nextResourceAccessId--;
		_resourceTypeToAccessId[resourceType] = id;
		return id;
	}

	private void EnsureSystemSetKnown(Type setType)
	{
		if (setType is null)
			throw new ArgumentNullException(nameof(setType));

		_setEnabledByType.TryAdd(setType, true);
	}

	private void RebuildExecutionPlan()
	{
		_phaseStagePlans.Clear();
		foreach (var entry in _planBuilder.Build(_systems))
			_phaseStagePlans[entry.Key] = entry.Value;

		_isPlanDirty = false;
		PlanBuildCount++;
	}
}

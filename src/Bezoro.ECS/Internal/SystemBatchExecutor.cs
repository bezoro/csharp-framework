using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;

namespace Bezoro.ECS.Internal;

internal sealed class SystemBatchExecutor(int maxDegreeOfParallelism)
{
	private const    int MAX_CATCH_UP_TICKS      = 3;
	private readonly int _maxDegreeOfParallelism = maxDegreeOfParallelism;
	// A world rejects overlapping updates. Workers only read this batch until the scheduler joins them.
	// Capacity tracks active systems; skipped systems do not reserve execution storage.
	private SystemExecution[] _executions = [];
	private int _executionCount;
	private World? _executionWorld;
	private Action<int>? _executeSystem;

	public void UpdatePhase(
		World                                          world,
		SystemLoopPhase                                loopPhase,
		Dictionary<Stage, List<SystemState[]>>         stagePlans,
		IReadOnlyDictionary<Type, bool>                setEnabledByType,
		IReadOnlyDictionary<Type, ISystemRunCondition> setRunConditionsByType,
		float                                          deltaTime)
	{
		foreach (var stage in StageOrder.Stages)
		{
			if (!stagePlans.TryGetValue(stage, out var stageBatches) || stageBatches.Count == 0)
				continue;

			for (var i = 0; i < stageBatches.Count; i++)
			{
				try
				{
					BuildExecutionBatch(
						world, loopPhase, stageBatches[i], setEnabledByType, setRunConditionsByType, deltaTime
					);

					if (_executionCount == 0)
						continue;

					ExecuteBatch(world);
					FlushStreams(world);
				}
				finally
				{
					DisposeStreams();
					Array.Clear(_executions, 0, _executionCount);
					_executionCount = 0;
				}
			}
		}
	}

	private static bool AreSystemSetsEnabled(Type[] systemSetTypes, IReadOnlyDictionary<Type, bool> setEnabledByType)
	{
		for (var i = 0; i < systemSetTypes.Length; i++)
		{
			if (setEnabledByType.TryGetValue(systemSetTypes[i], out bool enabled) && !enabled)
				return false;
		}

		return true;
	}

	private static bool EvaluateRunConditions(
		World                                          world,
		SystemLoopPhase                                loopPhase,
		SystemState                                    state,
		IReadOnlyDictionary<Type, ISystemRunCondition> setRunConditionsByType,
		float                                          deltaTime)
	{
		if (state.RunConditions.Length == 0 && state.SystemSetTypes.Length == 0)
			return true;

		var context = new SystemRunConditionContext(world, state.System, loopPhase, state.Stage, deltaTime);
		for (var i = 0; i < state.RunConditions.Length; i++)
		{
			if (!state.RunConditions[i].ShouldRun(in context))
				return false;
		}

		for (var i = 0; i < state.SystemSetTypes.Length; i++)
		{
			if (setRunConditionsByType.TryGetValue(state.SystemSetTypes[i], out var runCondition) &&
				!runCondition.ShouldRun(in context))
				return false;
		}

		return true;
	}

	private static void ExecuteSystem(SystemExecution execution, World world)
	{
		var state = execution.State;
		long epoch = state.BeginExecution();
		try
		{
			var context = new SystemContext(execution.DeltaTime, state.Stage, world, state, epoch);
			state.System.Update(in context);
		}
		finally { state.EndExecution(); }
	}

	private void ExecuteSystemAtIndex(int index) => ExecuteSystem(_executions[index], _executionWorld!);

	private void FlushStreams(World world)
	{
		for (var i = 0; i < _executionCount; i++)
		{
			var stream = _executions[i].State.RecordedCommandStream;
			if (stream is not null && stream.HasCommands)
				world.Playback(stream);
		}
	}

	private void DisposeStreams()
	{
		for (var i = 0; i < _executionCount; i++)
			_executions[i].State.DisposeCommandStream();
	}

	private bool ShouldRun(
		World                                          world,
		SystemLoopPhase                                loopPhase,
		SystemState                                    state,
		IReadOnlyDictionary<Type, bool>                setEnabledByType,
		IReadOnlyDictionary<Type, ISystemRunCondition> setRunConditionsByType,
		float                                          deltaTime,
		out float                                      effectiveDeltaTime)
	{
		if (!AreSystemSetsEnabled(state.SystemSetTypes, setEnabledByType) ||
			!EvaluateRunConditions(world, loopPhase, state, setRunConditionsByType, deltaTime))
		{
			effectiveDeltaTime = 0f;
			return false;
		}

		var settings = state.System.UpdateSettings;
		if (settings.IntervalSeconds <= 0f)
		{
			effectiveDeltaTime = deltaTime;
			return true;
		}

		state.Accumulator += deltaTime;
		double maxAccumulator = (double)settings.IntervalSeconds * MAX_CATCH_UP_TICKS;
		if (state.Accumulator > maxAccumulator)
			state.Accumulator = maxAccumulator;

		if (state.Accumulator < settings.IntervalSeconds)
		{
			effectiveDeltaTime = 0f;
			return false;
		}

		state.Accumulator  -= settings.IntervalSeconds;
		effectiveDeltaTime =  settings.IntervalSeconds;
		return true;
	}

	private void ExecuteBatch(World world)
	{
		world.SetConcurrentSystemBatch(_maxDegreeOfParallelism > 1 && _executionCount > 1);
		try
		{
			if (_maxDegreeOfParallelism == 1 || _executionCount == 1)
			{
				for (var i = 0; i < _executionCount; i++)
					ExecuteSystem(_executions[i], world);
			}
			else
			{
				_executionWorld = world;
				ParallelWorkScheduler.Execute(
					_executionCount,
					_maxDegreeOfParallelism,
					_executeSystem ??= ExecuteSystemAtIndex
				);
			}
		}
		finally
		{
			_executionWorld = null;
			world.SetConcurrentSystemBatch(false);
		}
	}

	private void BuildExecutionBatch(
		World                                          world,
		SystemLoopPhase                                loopPhase,
		SystemState[]                                  batchStates,
		IReadOnlyDictionary<Type, bool>                setEnabledByType,
		IReadOnlyDictionary<Type, ISystemRunCondition> setRunConditionsByType,
		float                                          deltaTime)
	{
		// Conflict checks belong to plan construction. Filtering a planned batch preserves its safety.
		for (var i = 0; i < batchStates.Length; i++)
		{
			var state = batchStates[i];
			if (!ShouldRun(
					world, loopPhase, state, setEnabledByType, setRunConditionsByType, deltaTime,
					out float effectiveDeltaTime
				))
				continue;

			if (_executionCount == _executions.Length)
			{
				int capacity = _executionCount == 0 ? 1 : (int)Math.Min((long)_executionCount * 2, batchStates.Length);
				Array.Resize(ref _executions, capacity);
			}
			_executions[_executionCount++] = new(state, effectiveDeltaTime);
		}
	}
}

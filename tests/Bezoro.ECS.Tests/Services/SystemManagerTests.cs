using System;
using System.Threading;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using JetBrains.Annotations;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

[TestSubject(typeof(SystemManager))]
public class SystemManagerTests
{
	[Fact]
	public void GetScheduleDiagnostics_WhenNoSystemsRegistered_ShouldReturnEmptyPlan()
	{
		using var world       = new World();
		var       diagnostics = world.GetScheduleDiagnostics();

		diagnostics.RegisteredSystemCount.Should().Be(0);
		diagnostics.PlanBuildCount.Should().Be(0);
		diagnostics.Phases.Should().BeEmpty();
	}

	[Fact]
	public void GetScheduleDiagnostics_WhenSystemsConflict_ShouldExposeSerializedBatches()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 1 });
		world.AddSystem(new ReadCounterSystem());
		world.AddSystem(new WriteCounterSystem(42));

		var diagnostics = world.GetScheduleDiagnostics();

		diagnostics.RegisteredSystemCount.Should().Be(2);
		diagnostics.PlanBuildCount.Should().Be(1);
		var tickPhase = diagnostics.Phases.Should().ContainSingle(phase => phase.LoopPhase == SystemLoopPhase.Tick)
								   .Subject;

		var tickStage = tickPhase.Stages.Should().ContainSingle(stage => stage.Stage == Stage.Tick)
								 .Subject;

		tickStage.Batches.Should().HaveCount(2);
		tickStage.Batches[0].SystemTypes.Should().ContainSingle()
				 .Which.Should().Be(typeof(ReadCounterSystem));

		tickStage.Batches[1].SystemTypes.Should().ContainSingle()
				 .Which.Should().Be(typeof(WriteCounterSystem));
	}

	[Fact]
	public void UpdateAll_ShouldRespectWriteReadDependenciesAcrossBatches()
	{
		// Arrange
		using var world  = new World(new WorldConfig { MaxDegreeOfParallelism = 1 });
		var       entity = world.Spawn();
		world.Add(entity, new Counter { Value = 1 });

		var preRead  = new ReadCounterSystem();
		var write    = new WriteCounterSystem(2);
		var postRead = new ReadCounterSystem();

		world.AddSystem(preRead);
		world.AddSystem(write);
		world.AddSystem(postRead);

		// Act
		world.Tick(1f / 60f);

		// Assert
		preRead.LastObserved.Should().Be(1);
		postRead.LastObserved.Should().Be(2);
	}

	[Fact]
	public void UpdateAll_WhenAccumulatedDeltaReachesInterval_ShouldRespectUpdateFrequency()
	{
		// Arrange
		using var world  = new World();
		var       system = new FixedStepSystem();
		world.AddSystem(system);

		// Act
		world.Tick(0.2f);
		world.Tick(0.29f);
		world.Tick(0.31f);

		// Assert
		system.UpdateCount.Should().Be(1);
		system.LastDeltaTime.Should().BeApproximately(0.5f, 0.0001f);
	}

	[Fact]
	public void UpdateAll_WhenDeltaTimeIsLarge_ShouldCapCatchUpTicks()
	{
		// Arrange
		using var world  = new World();
		var       system = new FixedStepSystem();
		world.AddSystem(system);

		// Act
		world.Tick(10f);
		world.Tick(0f);
		world.Tick(0f);
		world.Tick(0f);

		// Assert
		system.UpdateCount.Should().Be(3);
		system.LastDeltaTime.Should().BeApproximately(0.5f, 0.0001f);
	}

	[Fact]
	public void UpdateAll_WhenDependenciesFormCycle_ShouldThrowInvalidOperationException()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 1 });
		world.AddSystem(new CyclicAfterSystemA());
		world.AddSystem(new CyclicAfterSystemB());

		var act = () => world.Tick(1f / 60f);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("*contains a cycle*");
	}

	[Fact]
	public void UpdateAll_WhenExclusiveSystemIsRegistered_ShouldRunAloneEvenAlongsideUndeclaredSystems()
	{
		// [Exclusive] systems must still run alone; fix 2a only affects undeclared systems.
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var       probe = new ConcurrencyProbe();

		world.AddSystem(new UndeclaredAccessProbeSystem(probe));
		world.AddSystem(new ExclusiveProbeSystem(probe));

		world.Tick(1f / 60f);

		// The exclusive system forces a batch break, so no two systems run at the same time.
		probe.MaxConcurrent.Should().Be(1);
	}

	[Fact]
	public void UpdateAll_WhenFixedStepSystemsUseDifferentPhases_ShouldAccumulateIndependently()
	{
		using var world = new World();
		var updateSystem = new PhaseCounterSystem(SystemLoopPhase.Tick, SystemUpdateSettings.FixedInterval(0.5f));
		var fixedSystem = new PhaseCounterSystem(SystemLoopPhase.FixedTick, SystemUpdateSettings.FixedInterval(0.5f));

		world.AddSystem(updateSystem);
		world.AddSystem(fixedSystem);

		world.Tick(0.3f);
		world.FixedTick(0.3f);
		world.Tick(0.3f);
		world.FixedTick(0.3f);

		updateSystem.UpdateCount.Should().Be(1);
		updateSystem.LastDeltaTime.Should().BeApproximately(0.5f, 0.0001f);
		fixedSystem.UpdateCount.Should().Be(1);
		fixedSystem.LastDeltaTime.Should().BeApproximately(0.5f, 0.0001f);
	}

	[Fact]
	public void UpdateAll_WhenParallelSystemThrows_ShouldRethrowOriginalException()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		world.AddSystem(new NoOpSystem());
		world.AddSystem(new ThrowingSystem());

		var act = () => world.Tick(1f / 60f);

		act.Should().Throw<InvalidOperationException>()
		   .WithMessage("system-fail");
	}

	[Fact]
	public void UpdateAll_WhenPlanIsDirty_ShouldBuildPlanOnceAndReuseAcrossFrames()
	{
		// Arrange
		using var world = new World();
		world.AddSystem(new ReadCounterSystem());
		world.AddSystem(new WriteCounterSystem(3));

		// Assert precondition
		world.SchedulerPlanBuildCount.Should().Be(0);

		// Act
		world.Tick(1f / 60f);
		world.Tick(1f / 60f);
		world.Tick(1f / 60f);

		// Assert
		world.SchedulerPlanBuildCount.Should().Be(1);
	}

	[Fact]
	public void UpdateAll_WhenReentrantTickAttemptFails_ShouldAllowSubsequentTicks()
	{
		using var world  = new World();
		var       system = new ReentrantTickSystem();
		world.AddSystem(system);

		world.Tick(1f / 60f);
		world.Tick(1f / 60f);

		system.ReentrantException.Should().BeOfType<InvalidOperationException>();
		system.UpdateCount.Should().Be(2);
	}

	[Fact]
	public void UpdateAll_WhenReusingCommandReferenceFromPreviousTick_ShouldThrowObjectDisposedException()
	{
		using var world  = new World();
		var       system = new StaleCommandStreamReferenceSystem();
		world.AddSystem(system);

		world.Tick(1f / 60f);
		world.Tick(1f / 60f);

		system.ReuseException.Should().BeOfType<ObjectDisposedException>();
	}

	[Fact]
	public void UpdateAll_WhenSetRunConditionIsFalse_ShouldSkipAllSystemsInSetUntilConditionBecomesTrue()
	{
		using var world = new World();
		world.SetResource(new SchedulerGateResource { Enabled = false });
		world.SetSystemSetRunCondition<SimulationSystemSet>(new SchedulerGateRunCondition());

		var first  = new SetCounterSystem();
		var second = new SecondarySetCounterSystem();
		world.AddSystem(first);
		world.AddSystem(second);

		world.Tick(1f / 60f);
		world.WriteResource<SchedulerGateResource>().Enabled = true;
		world.Tick(1f / 60f);

		first.UpdateCount.Should().Be(1);
		second.UpdateCount.Should().Be(1);
	}

	[Fact]
	public void UpdateAll_WhenSystemCapturesCommands_ShouldDisposeCommandStreamAfterFlush()
	{
		using var world  = new World();
		var       system = new CommandCaptureSystem();
		world.AddSystem(system);

		world.Tick(1f / 60f);

		world.EntityCount.Should().Be(1);
		system.Captured.Should().NotBeNull();
		var act = () => system.Captured!.CreateEntity();
		act.Should().Throw<ObjectDisposedException>();
	}

	[Fact]
	public void UpdateAll_WhenSystemDeclaresAfterDependency_ShouldRunAfterTargetEvenWhenRegisteredFirst()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 1 });
		var       probe = new OrderingProbe();
		world.AddSystem(new AfterDependencySystem(probe));
		world.AddSystem(new AnchorDependencySystem(probe));

		world.Tick(1f / 60f);

		probe.AnchorOrder.Should().Be(1);
		probe.AfterOrder.Should().Be(2);
	}

	[Fact]
	public void UpdateAll_WhenSystemDeclaresBeforeDependency_ShouldRunBeforeTargetEvenWhenRegisteredLast()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 1 });
		var       probe = new OrderingProbe();
		world.AddSystem(new BeforeTargetSystem(probe));
		world.AddSystem(new BeforeDependencySystem(probe));

		world.Tick(1f / 60f);

		probe.BeforeOrder.Should().Be(1);
		probe.TargetOrder.Should().Be(2);
	}

	[Fact]
	public void UpdateAll_WhenSystemDoesNotRecordCommands_ShouldKeepCommandStorageUnallocated()
	{
		using var world  = new World();
		var       system = new CommandStreamAllocationProbeSystem();
		world.AddSystem(system);

		world.Tick(1f / 60f);

		system.RecordedCommands.Should().Be(0);
	}

	[Fact]
	public void UpdateAll_WhenSystemLoopPhaseIsFixedUpdate_ShouldNotRunDuringUpdate()
	{
		using var world  = new World();
		var       system = new PhaseCounterSystem(SystemLoopPhase.FixedTick, SystemUpdateSettings.EveryTick);
		world.AddSystem(system);

		world.Tick(1f / 60f);

		system.UpdateCount.Should().Be(0);
	}

	[Fact]
	public void UpdateAll_WhenSystemLoopPhaseIsLateUpdate_ShouldRunOnlyDuringLateUpdate()
	{
		using var world  = new World();
		var       system = new PhaseCounterSystem(SystemLoopPhase.LateTick, SystemUpdateSettings.EveryTick);
		world.AddSystem(system);

		world.Tick(1f / 60f);
		world.FixedTick(1f / 50f);
		world.LateTick(1f / 60f);

		system.UpdateCount.Should().Be(1);
		system.LastDeltaTime.Should().BeApproximately(1f / 60f, 0.0001f);
	}

	[Fact]
	public void UpdateAll_WhenSystemReentersTick_ShouldThrowInvalidOperationException()
	{
		using var world  = new World();
		var       system = new ReentrantTickSystem();
		world.AddSystem(system);

		world.Tick(1f / 60f);

		system.ReentrantException.Should().BeOfType<InvalidOperationException>();
		system.UpdateCount.Should().Be(1);
	}

	[Fact]
	public void UpdateAll_WhenSystemRunConditionIsFalse_ShouldSkipSystemUntilConditionBecomesTrue()
	{
		using var world = new World();
		world.SetResource(new SchedulerGateResource { Enabled = false });
		var system = new ConditionallyEnabledCounterSystem();
		world.AddSystem(system);

		world.Tick(1f / 60f);
		world.WriteResource<SchedulerGateResource>().Enabled = true;
		world.Tick(1f / 60f);

		system.UpdateCount.Should().Be(1);
	}

	[Fact]
	public void UpdateAll_WhenSystemsChangeAfterFirstUpdate_ShouldRebuildPlanOnNextUpdate()
	{
		// Arrange
		using var world = new World();
		world.AddSystem(new ReadCounterSystem());
		world.Tick(1f / 60f);
		world.SchedulerPlanBuildCount.Should().Be(1);

		// Act
		world.AddSystem(new WriteCounterSystem(5));
		world.SchedulerPlanBuildCount.Should().Be(1);
		world.Tick(1f / 60f);

		// Assert
		world.SchedulerPlanBuildCount.Should().Be(2);
	}

	[Fact]
	public void UpdateAll_WhenSystemsDeclareReadMetadata_ShouldAllowParallelExecution()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var       probe = ConcurrencyProbe.ForParallelAssertion();
		world.AddSystem(new DeclaredReadProbeSystem(probe));
		world.AddSystem(new DeclaredReadProbeSystem(probe));

		world.Tick(1f / 60f);

		probe.MaxConcurrent.Should().BeGreaterThan(1);
	}

	[Fact]
	public void UpdateAll_WhenSystemsDeclareResourceReadMetadata_ShouldAllowParallelExecution()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var       probe = ConcurrencyProbe.ForParallelAssertion();
		world.AddSystem(new ResourceReadProbeSystem(probe));
		world.AddSystem(new ResourceReadProbeSystem(probe));

		world.Tick(1f / 60f);

		probe.MaxConcurrent.Should().BeGreaterThan(1);
	}

	[Fact]
	public void UpdateAll_WhenSystemSetIsDisabled_ShouldSkipSystemsInSet()
	{
		using var world           = new World();
		var       setSystem       = new SetCounterSystem();
		var       alwaysRunSystem = new NoSetCounterSystem();
		world.AddSystem(setSystem);
		world.AddSystem(alwaysRunSystem);

		world.Tick(1f / 60f);
		world.SetSystemSetEnabled<SimulationSystemSet>(false);
		world.Tick(1f / 60f);
		world.SetSystemSetEnabled<SimulationSystemSet>(true);
		world.Tick(1f / 60f);

		setSystem.UpdateCount.Should().Be(2);
		alwaysRunSystem.UpdateCount.Should().Be(3);
	}

	[Fact]
	public void UpdateAll_WhenSystemsHaveNoMetadata_ShouldRunExclusively()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var       probe = new ConcurrencyProbe();
		world.AddSystem(new UndeclaredAccessProbeSystem(probe));
		world.AddSystem(new UndeclaredAccessProbeSystem(probe));

		world.Tick(1f / 60f);

		probe.MaxConcurrent.Should().Be(1);
	}

	[Fact]
	public void UpdateAll_WhenSystemWritesResourceAndAnotherReadsResource_ShouldSerializeExecution()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var       probe = new ConcurrencyProbe();
		world.AddSystem(new ResourceWriteProbeSystem(probe));
		world.AddSystem(new ResourceReadProbeSystem(probe));

		world.Tick(1f / 60f);

		probe.MaxConcurrent.Should().Be(1);
	}

	[Fact]
	public void UpdateAll_WhenUndeclaredAndDeclaredReadSystemsShareStage_ShouldKeepUnknownAccessExclusive()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var       probe = new ConcurrencyProbe();

		world.AddSystem(new UndeclaredAccessProbeSystem(probe));
		world.AddSystem(new DeclaredReadProbeSystem(probe));

		world.Tick(1f / 60f);

		probe.MaxConcurrent.Should().Be(1);
	}

	[Fact]
	public void UpdateAll_WhenSystemsDeclareNoWorldAccess_ShouldAllowParallelExecution()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var probe = ConcurrencyProbe.ForParallelAssertion();
		world.AddSystem(new IndependentProbeSystem(probe));
		world.AddSystem(new IndependentProbeSystem(probe));
		world.Tick(0.1f);
		probe.MaxConcurrent.Should().Be(2);
	}

	[NoWorldAccess]
	private sealed class IndependentProbeSystem(ConcurrencyProbe probe) : ISystem
	{
		public void Update(in SystemContext context) => probe.Enter();
	}

	[After(typeof(AnchorDependencySystem))]
	private sealed class AfterDependencySystem(OrderingProbe probe) : ISystem
	{
		public void Update(in SystemContext context)
		{
			probe.AfterOrder = probe.NextOrder();
		}
	}

	private sealed class AnchorDependencySystem(OrderingProbe probe) : ISystem
	{
		public void Update(in SystemContext context)
		{
			probe.AnchorOrder = probe.NextOrder();
		}
	}

	[Before(typeof(BeforeTargetSystem))]
	private sealed class BeforeDependencySystem(OrderingProbe probe) : ISystem
	{
		public void Update(in SystemContext context)
		{
			probe.BeforeOrder = probe.NextOrder();
		}
	}

	private sealed class BeforeTargetSystem(OrderingProbe probe) : ISystem
	{
		public void Update(in SystemContext context)
		{
			probe.TargetOrder = probe.NextOrder();
		}
	}

	private sealed class CommandStreamAllocationProbeSystem : ISystem
	{
		public int RecordedCommands { get; private set; }

		public void Update(in SystemContext context)
		{
			RecordedCommands = context.CommandStream.GetDiagnostics().RecordedCommands;
		}
	}

	private sealed class CommandCaptureSystem : ISystem
	{
		public CommandStream? Captured { get; private set; }

		public void Update(in SystemContext context)
		{
			Captured = context.CommandStream;
			context.CommandStream.CreateEntity();
		}
	}

	private sealed class ConcurrencyProbe(TimeSpan firstWorkerWait)
	{
		private readonly ManualResetEventSlim _release = new(false);
		private          int                  _maxConcurrent;
		private          int                  _running;

		public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

		public ConcurrencyProbe() : this(TimeSpan.FromMilliseconds(200)) { }

		public static ConcurrencyProbe ForParallelAssertion() =>
			new(TimeSpan.FromSeconds(5));

		public void Enter()
		{
			int running = Interlocked.Increment(ref _running);
			UpdateMax(running);

			if (running == 1)
				_release.Wait(firstWorkerWait);
			else
				_release.Set();

			Interlocked.Decrement(ref _running);
		}

		private void UpdateMax(int candidate)
		{
			while (true)
			{
				int current = Volatile.Read(ref _maxConcurrent);
				if (candidate <= current)
					return;

				if (Interlocked.CompareExchange(ref _maxConcurrent, candidate, current) == current)
					return;
			}
		}
	}

	[RunIf(typeof(SchedulerGateRunCondition))]
	private sealed class ConditionallyEnabledCounterSystem : ISystem
	{
		public int UpdateCount { get; private set; }

		public void Update(in SystemContext context) =>
			UpdateCount++;
	}

	private readonly struct Counter
	{
		public int Value { get; init; }
	}

	private readonly struct CounterQuerySpec : ICompiledQuerySpec
	{
		public void Build(ref QueryBuilder builder) => builder.All<Counter>();
	}

	[After(typeof(CyclicAfterSystemB))]
	private sealed class CyclicAfterSystemA : ISystem
	{
		public void Update(in SystemContext context) { }
	}

	[After(typeof(CyclicAfterSystemA))]
	private sealed class CyclicAfterSystemB : ISystem
	{
		public void Update(in SystemContext context) { }
	}

	[Reads(typeof(Counter))]
	private sealed class DeclaredReadProbeSystem(ConcurrencyProbe probe) : ISystem
	{
		public void Update(in SystemContext context) =>
			probe.Enter();
	}

	[Exclusive]
	private sealed class ExclusiveProbeSystem(ConcurrencyProbe probe) : ISystem
	{
		public void Update(in SystemContext context) =>
			probe.Enter();
	}

	private sealed class FixedStepSystem : ISystem
	{
		public SystemUpdateSettings UpdateSettings => SystemUpdateSettings.FixedInterval(0.5f);
		public float                LastDeltaTime  { get; private set; }
		public int                  UpdateCount    { get; private set; }

		public void Update(in SystemContext context)
		{
			UpdateCount++;
			LastDeltaTime = context.DeltaTime;
		}
	}

	private sealed class NoOpSystem : ISystem
	{
		public void Update(in SystemContext context) { }
	}

	private sealed class NoSetCounterSystem : ISystem
	{
		public int UpdateCount { get; private set; }

		public void Update(in SystemContext context) =>
			UpdateCount++;
	}

	private sealed class OrderingProbe
	{
		private int _step;

		public int AfterOrder  { get; set; }
		public int AnchorOrder { get; set; }
		public int BeforeOrder { get; set; }
		public int TargetOrder { get; set; }

		public int NextOrder() => Interlocked.Increment(ref _step);
	}

	private sealed class PhaseCounterSystem(SystemLoopPhase loopPhase, SystemUpdateSettings updateSettings) : ISystem
	{
		public SystemLoopPhase      LoopPhase      { get; } = loopPhase;
		public SystemUpdateSettings UpdateSettings { get; } = updateSettings;
		public float                LastDeltaTime  { get; private set; }
		public int                  UpdateCount    { get; private set; }

		public void Update(in SystemContext context)
		{
			UpdateCount++;
			LastDeltaTime = context.DeltaTime;
		}
	}

	[Reads(typeof(Counter))]
	private sealed class ReadCounterSystem : ISystem
	{
		private QueryHandle<CounterQuerySpec> _query;

		public SystemUpdateSettings UpdateSettings => SystemUpdateSettings.EveryTick;
		public int                  LastObserved   { get; private set; } = -1;

		public void OnCreate(World world)
		{
			if (world is null) throw new ArgumentNullException(nameof(world));

			_query = world.Compile<CounterQuerySpec>();
		}

		public void Update(in SystemContext context)
		{
			using var cursor = context.World.Execute(_query);
			if (!cursor.MoveNext() || cursor.Current.Length == 0)
				return;

			LastObserved = cursor.Read<Counter>(0).Value;
		}
	}

	private sealed class ReentrantTickSystem : ISystem
	{
		private bool _attempted;

		public Exception? ReentrantException { get; private set; }
		public int        UpdateCount        { get; private set; }

		public void Update(in SystemContext context)
		{
			UpdateCount++;
			if (_attempted) return;

			_attempted = true;
			try
			{
				context.World.Tick(0f);
			}
			catch (Exception ex)
			{
				ReentrantException = ex;
			}
		}
	}

	[ReadsResource(typeof(SchedulerResource))]
	private sealed class ResourceReadProbeSystem(ConcurrencyProbe probe) : ISystem
	{
		public void Update(in SystemContext context) =>
			probe.Enter();
	}

	[WritesResource(typeof(SchedulerResource))]
	private sealed class ResourceWriteProbeSystem(ConcurrencyProbe probe) : ISystem
	{
		public void Update(in SystemContext context) =>
			probe.Enter();
	}

	private sealed class SchedulerGateResource
	{
		public bool Enabled { get; set; }
	}

	private sealed class SchedulerGateRunCondition : ISystemRunCondition
	{
		public bool ShouldRun(in SystemRunConditionContext context) =>
			context.World.ReadResource<SchedulerGateResource>().Enabled;
	}

	private sealed class SchedulerResource;

	[SystemSet(typeof(SimulationSystemSet))]
	private sealed class SecondarySetCounterSystem : ISystem
	{
		public int UpdateCount { get; private set; }

		public void Update(in SystemContext context) =>
			UpdateCount++;
	}

	[SystemSet(typeof(SimulationSystemSet))]
	private sealed class SetCounterSystem : ISystem
	{
		public int UpdateCount { get; private set; }

		public void Update(in SystemContext context) =>
			UpdateCount++;
	}

	private sealed class SimulationSystemSet;

	private sealed class StaleCommandStreamReferenceSystem : ISystem
	{
		private CommandStream? _previous;

		public Exception? ReuseException { get; private set; }

		public void Update(in SystemContext context)
		{
			if (_previous is { } && ReuseException is null)
				try
				{
					_previous.CreateEntity();
				}
				catch (Exception ex)
				{
					ReuseException = ex;
				}

			_previous = context.CommandStream;
		}
	}

	private sealed class ThrowingSystem : ISystem
	{
		public void Update(in SystemContext context) =>
			throw new InvalidOperationException("system-fail");
	}

	private sealed class UndeclaredAccessProbeSystem(ConcurrencyProbe probe) : ISystem
	{
		public void Update(in SystemContext context) =>
			probe.Enter();
	}

	[Writes(typeof(Counter))]
	private sealed class WriteCounterSystem(int value) : ISystem
	{
		private QueryHandle<CounterQuerySpec> _query;

		public SystemUpdateSettings UpdateSettings => SystemUpdateSettings.EveryTick;

		public void OnCreate(World world)
		{
			if (world is null) throw new ArgumentNullException(nameof(world));

			_query = world.Compile<CounterQuerySpec>();
		}

		public void Update(in SystemContext context)
		{
			using var cursor = context.World.Execute(_query);
			if (!cursor.MoveNext())
				return;

			for (var i = 0; i < cursor.Current.Length; i++)
				cursor.Write<Counter>(i) = new() { Value = value };
		}
	}
}

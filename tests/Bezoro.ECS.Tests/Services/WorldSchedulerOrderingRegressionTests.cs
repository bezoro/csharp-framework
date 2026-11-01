using System;
using System.Collections.Generic;
using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

public sealed class WorldSchedulerOrderingRegressionTests
{
	[Fact]
	public void ExplicitAfter_OverridesRegistrationOrderForConflictingSystems()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var calls = new List<string>();
		world.AddSystem(new Dependent(calls));
		world.AddSystem(new Prerequisite(calls));
		world.Tick(0.1f);
		calls.Should().Equal("prerequisite", "dependent");
	}

	[Fact]
	public void SystemUpdate_CannotDisposeItsWorldBeforeCommandFlush()
	{
		using var world = new World();
		world.AddSystem(new DisposingSystem());
		Action tick = () => world.Tick(0.1f);
		tick.Should().Throw<InvalidOperationException>().WithMessage("*system update*");
		world.EntityCount.Should().Be(0);
	}

	[Fact]
	public void UnknownPhase_IsRejectedEvenWhenTheWorldHasNoSystems()
	{
		using var world = new World();
		Action run = () => world.RunPhase((SystemLoopPhase)123, 0f);
		run.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Theory]
	[InlineData("spawn")]
	[InlineData("reset")]
	[InlineData("clear")]
	[InlineData("add")]
	[InlineData("register system")]
	[InlineData("enable set")]
	[InlineData("set condition")]
	[InlineData("clear condition")]
	public void ConcurrentBatch_RejectsStructuralOperationsForAttributeOnlySystems(string operation)
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var entity = world.Spawn();
		Action<World> mutate = operation switch
		{
			"spawn" => current => current.Spawn(),
			"reset" => current => current.Reset(),
			"clear" => current => current.Clear(),
			"register system" => current => current.AddSystem(new IndependentSystem()),
			"enable set" => current => current.SetSystemSetEnabled<StageAwareSystem>(false),
			"set condition" => current => current.SetSystemSetRunCondition<StageAwareSystem>(new AlwaysRun()),
			"clear condition" => current => current.ClearSystemSetRunCondition<StageAwareSystem>(),
			_ => current => current.Add(entity, 42)
		};
		world.AddSystem(new AttributeOnlyStructuralSystem(mutate));
		world.AddSystem(new IndependentSystem());
		Action tick = () => world.Tick(0.1f);
		tick.Should().Throw<InvalidOperationException>().WithMessage("*concurrent system batch*");
		world.EntityCount.Should().Be(1);
		world.Has<int>(entity).Should().BeFalse();
		world.Add(entity, 42);
		world.Read<int>(entity).Should().Be(42, "the batch guard must release after a worker fails");
	}

	private sealed class AlwaysRun : ISystemRunCondition
	{
		public bool ShouldRun(in SystemRunConditionContext context) => true;
	}

	[Fact]
	public void AddSystem_UsesTheSystemStageUnlessAnOverrideIsSupplied()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 1 });
		var declared = new StageAwareSystem();
		var overridden = new StageAwareSystem();
		world.AddSystem(declared);
		world.AddSystem(overridden, Stage.Input);
		world.Tick(0.1f);
		declared.ObservedStage.Should().Be(Stage.Render);
		overridden.ObservedStage.Should().Be(Stage.Input);
		declared.Count.Should().Be(1);
		overridden.Count.Should().Be(1);
	}

	[Fact]
	public void InferredAccess_DescribesTypesWithoutAuthorizingParallelExecution()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var entity = world.Spawn(new ErgonomicJobPosition());
		world.AddSystem(new WriteComponentSystem(entity));
		var diagnostics = world.GetScheduleDiagnostics();
		diagnostics.Phases[0].Stages[0].Batches[0].ContainsExclusiveSystem.Should().BeTrue();
		world.Tick(0.1f);
		world.Read<ErgonomicJobPosition>(entity).Value.Should().Be(1);
	}

	[Writes(typeof(int))]
	private sealed class AttributeOnlyStructuralSystem(Action<World> mutate) : ISystem
	{
		public void Update(in SystemContext context) => mutate(context.World);
	}
	[NoWorldAccess]
	private sealed class IndependentSystem : ISystem
	{
		public void Update(in SystemContext context) { }
	}
	[NoWorldAccess]
	private sealed class StageAwareSystem : ISystem
	{
		public Stage Stage => Stage.Render;
		public Stage ObservedStage { get; private set; }
		public int Count { get; private set; }
		public void Update(in SystemContext context) { ObservedStage = context.Stage; Count++; }
	}

	[Writes(typeof(int)), After(typeof(Prerequisite))]
	private sealed class Dependent(List<string> calls) : ISystem
	{
		public void Update(in SystemContext context) => calls.Add("dependent");
	}
	[Writes(typeof(int))]
	private sealed class Prerequisite(List<string> calls) : ISystem
	{
		public void Update(in SystemContext context) => calls.Add("prerequisite");
	}
	private sealed class DisposingSystem : ISystem
	{
		public void Update(in SystemContext context) => context.World.Dispose();
	}
}

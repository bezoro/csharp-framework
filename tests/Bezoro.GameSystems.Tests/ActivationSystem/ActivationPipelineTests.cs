using System;
using System.Collections.Generic;
using System.Linq;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Bezoro.GameSystems.ActivationSystem.Extensions;
using Bezoro.GameSystems.ActivationSystem.Services;
using Bezoro.GameSystems.ActivationSystem.Types;
using FluentAssertions;
using JetBrains.Annotations;
using Xunit;

namespace Bezoro.GameSystems.Tests.ActivationSystem;

[TestSubject(typeof(ActivationProcessingSystem))]
public class ActivationPipelineTests
{
	[Fact]
	public void FullCompletionQueueDoesNotBlockActivationDispatch()
	{
		using var world = new World();
		var events = new ActivationEventsResource(maxCapacity: 1);
		events.Enqueue(new ActivationCompletedEvent(10));
		world.SetResource(events);
		world.AddActivationPipeline();
		int activations = 0;
		world.GetOrCreateActivationCommandQueue().Register(() => activations++);

		world.Tick(0f);
		Assert.Equal(1, activations);
		Assert.True(events.TryDequeue(out var previous));
		Assert.Equal(10, previous.ActivatedCount);

		world.Tick(0f);
		Assert.True(events.TryDequeue(out var completed));
		Assert.Equal(1, completed.ActivatedCount);
		world.Tick(0f);
		Assert.Equal(0, events.Count);
	}

	[Fact]
	public void SmallBatchesRetainHighestPriorityAcrossMultipleCandidateGroups()
	{
		using var world = new World();
		world.AddActivationPipeline();
		world.SetResource(new ActivationConfig(2));
		var queue = world.GetOrCreateActivationCommandQueue();
		var order = new System.Collections.Generic.List<int>();
		foreach (int priority in new[] { 1, 4, 8, 2, 5, 9, 7, 6, 3 })
			queue.Register(() => order.Add(priority), priority);
		world.Tick(0.1f);
		world.Tick(0.1f);
		Assert.Equal(new[] { 9, 8, 7, 6 }, order);
	}

	[Fact]
	public void Metadata_WhenInspectingActivationProcessingSystem_ShouldDeclareReadAndWriteAttributes()
	{
		var systemType = typeof(ActivationProcessingSystem);

		systemType.GetCustomAttributes(typeof(WritesAttribute), true)
				  .Cast<WritesAttribute>()
				  .Any(attribute => attribute.ComponentType == typeof(ActivationEntry))
				  .Should()
				  .BeTrue();
		systemType.GetCustomAttributes(typeof(ReadsAttribute), true)
				  .Cast<ReadsAttribute>()
				  .Any(attribute => attribute.ComponentType == typeof(ActivationCancellationRequest))
				  .Should()
				  .BeTrue();
		systemType.GetCustomAttributes(typeof(ReadsResourceAttribute), true)
				  .Cast<ReadsResourceAttribute>()
				  .Any(attribute => attribute.ResourceType == typeof(ActivationConfig))
				  .Should()
				  .BeTrue();
		systemType.GetCustomAttributes(typeof(WritesResourceAttribute), true)
				  .Cast<WritesResourceAttribute>()
				  .Any(attribute => attribute.ResourceType == typeof(ActivationRuntimeState))
				  .Should()
				  .BeTrue();
		systemType.GetCustomAttributes(typeof(WritesResourceAttribute), true)
				  .Cast<WritesResourceAttribute>()
				  .Any(attribute => attribute.ResourceType == typeof(ActivationEventsResource))
				  .Should()
				  .BeTrue();
		systemType.GetCustomAttributes(typeof(WritesResourceAttribute), true)
				  .Cast<WritesResourceAttribute>()
				  .Any(attribute => attribute.ResourceType == typeof(ActivationDispatchQueueResource))
				  .Should()
				  .BeTrue();
	}

	[Fact]
	public void Tick_WhenDispatchMayRecreateActivationConfig_ShouldSerializeConfigReaders()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var reader = new ActivationConfigReaderSystem();
		world.AddSystem(new ActivationDispatchSystem(), Stage.PostTick);
		world.AddSystem(reader,                         Stage.PostTick);
		world.RemoveResource<ActivationConfig>().Should().BeTrue();

		var diagnostics = world.GetScheduleDiagnostics();
		var tickPhase = diagnostics.Phases.Should()
								   .ContainSingle(phase => phase.LoopPhase == SystemLoopPhase.Tick)
								   .Subject;
		var postTickStage = tickPhase.Stages.Should()
								  .ContainSingle(stage => stage.Stage == Stage.PostTick)
								  .Subject;

		postTickStage.Batches.Should().HaveCount(2);
		postTickStage.Batches[0].SystemTypes.Should().ContainSingle()
					 .Which.Should().Be(typeof(ActivationDispatchSystem));
		postTickStage.Batches[1].SystemTypes.Should().ContainSingle()
					 .Which.Should().Be(typeof(ActivationConfigReaderSystem));

		world.Tick(0f);

		world.HasResource<ActivationConfig>().Should().BeTrue();
		reader.ObservedConfig.Should().BeTrue();
	}

	[Fact]
	public void Tick_WhenActivationCompletes_ShouldPublishCompletionOncePerCompletionEdge()
	{
		var world = new World();
		world.SetResource(new ActivationConfig(2));
		world.AddActivationPipeline();
		var queue = world.GetOrCreateActivationCommandQueue();

		queue.Register(() => { });
		queue.Register(() => { });
		queue.Register(() => { });

		world.Tick(0f);
		world.Tick(0f);
		world.Tick(0f);

		queue.Register(() => { });
		world.Tick(0f);

		var events = world.ReadResource<ActivationEventsResource>();
		events.Count.Should().Be(2);
	}

	[Fact]
	public void Tick_WhenCallbacksAreDispatchedThroughConfiguredDispatcher_ShouldDeferInvocationToDispatcher()
	{
		var dispatched = new List<Action>();
		var world      = new World();
		world.SetResource(new ActivationConfig(callbackDispatcher: callback => dispatched.Add(callback)));
		world.AddActivationPipeline();
		var queue   = world.GetOrCreateActivationCommandQueue();
		var invoked = false;

		queue.Register(() => invoked = true);
		world.Tick(0f);

		invoked.Should().BeFalse();
		dispatched.Should().HaveCount(1);

		dispatched[0]();
		invoked.Should().BeTrue();
	}

	[Fact]
	public void Tick_WhenCancellationWasQueuedBeforeProcessing_ShouldNotInvokeCallback()
	{
		var world = new World();
		world.AddActivationPipeline();
		var queue   = world.GetOrCreateActivationCommandQueue();
		var invoked = false;

		var handle = queue.Register(() => invoked = true);
		queue.Cancel(handle).Should().BeTrue();

		world.Tick(0f);

		invoked.Should().BeFalse();
		var runtime = world.ReadResource<ActivationRuntimeState>();
		runtime.ActivatedCount.Should().Be(0);
		runtime.PendingCount.Should().Be(0);
	}

	[Fact]
	public void Tick_WhenMaxActivationsPerTickIsLimited_ShouldRespectBudgetPerTick()
	{
		var world = new World();
		world.SetResource(new ActivationConfig(2));
		world.AddActivationPipeline();
		var queue     = world.GetOrCreateActivationCommandQueue();
		var activated = 0;

		for (var i = 0; i < 5; i++)
			queue.Register(() => activated++);

		world.Tick(0f);
		activated.Should().Be(2);

		world.Tick(0f);
		activated.Should().Be(4);

		world.Tick(0f);
		activated.Should().Be(5);
	}

	[Fact]
	public void Tick_WhenPrioritiesDiffer_ShouldActivateInPriorityThenRegistrationOrder()
	{
		var order = new List<string>();
		var world = new World();
		world.SetResource(new ActivationConfig(1));
		world.AddActivationPipeline();
		var queue = world.GetOrCreateActivationCommandQueue();

		queue.Register(() => order.Add("low"));
		queue.Register(() => order.Add("high"),                 10);
		queue.Register(() => order.Add("medium"),               5);
		queue.Register(() => order.Add("same-priority-first"),  1);
		queue.Register(() => order.Add("same-priority-second"), 1);

		for (var i = 0; i < 5; i++)
			world.Tick(0f);

		order.Should().Equal("high", "medium", "same-priority-first", "same-priority-second", "low");

		var runtime = world.ReadResource<ActivationRuntimeState>();
		runtime.ActivatedCount.Should().Be(5);
		runtime.PendingCount.Should().Be(0);
		runtime.IsComplete.Should().BeTrue();
	}
}

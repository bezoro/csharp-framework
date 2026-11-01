using Bezoro.ECS.Abstractions;
using Bezoro.ECS.Attributes;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using FluentAssertions;
using JetBrains.Annotations;
using Xunit;

namespace Bezoro.ECS.Tests.Services;

[TestSubject(typeof(SystemManager))]
public class SystemManagerErgonomicInferenceTests
{
	[Fact]
	public void UpdateAll_WhenSystemsUseExplicitResourceReadAndWriteApis_ShouldSerializeExecution()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var       probe = new ErgonomicConcurrencyProbe();
		world.SetResource(new ErgonomicSchedulerResource());

		world.AddSystem(new ErgonomicReadResourceSystem(probe));
		world.AddSystem(new ErgonomicWriteResourceSystem(probe));

		world.Tick(1f / 60f);

		probe.MaxConcurrent.Should().Be(1);
	}

	[Fact]
	public void UpdateAll_WhenSystemsUseGeneratedEntityAwareQueryViewJobs_ShouldSerializeExecution()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var       probe = new ErgonomicConcurrencyProbe();

		world.Spawn(
			new ErgonomicJobPosition { Value = 1 },
			new ErgonomicJobVelocity { Value = 2 }
		);

		world.AddSystem(new ErgonomicEntityAwareJobSystem(probe));
		world.AddSystem(new ErgonomicRegularJobSystem(probe));

		world.Tick(1f / 60f);

		probe.MaxConcurrent.Should().Be(1);
	}

	[Fact]
	public void UpdateAll_WhenSystemsUseReadOnlyResourceApisOnSameType_ShouldAllowConcurrentExecution()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var       probe = ErgonomicConcurrencyProbe.ForParallelAssertion();
		world.SetResource(new ErgonomicSchedulerResource());

		world.AddSystem(new ErgonomicReadResourceSystem(probe));
		world.AddSystem(new ErgonomicReadResourceSystemB(probe));

		world.Tick(1f / 60f);

		probe.MaxConcurrent.Should().Be(2);
	}

	[Fact]
	public void UpdateAll_WhenSystemsUseGeneratedEntityAwareWorldJobs_ShouldSerializeExecution()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var       probe = new ErgonomicConcurrencyProbe();

		world.Spawn(
			new ErgonomicJobPosition { Value = 1 },
			new ErgonomicJobVelocity { Value = 2 }
		);

		world.AddSystem(new ErgonomicEntityAwareWorldJobSystem(probe));
		world.AddSystem(new ErgonomicRegularWorldJobSystem(probe));

		world.Tick(1f / 60f);

		probe.MaxConcurrent.Should().Be(1);
	}

	[Fact]
	public void UpdateAll_WhenSystemsUseGeneratedEntityAwareCursorJobs_ShouldSerializeExecution()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var       probe = new ErgonomicConcurrencyProbe();

		world.Spawn(
			new ErgonomicJobPosition { Value = 1 },
			new ErgonomicJobVelocity { Value = 2 }
		);

		world.AddSystem(new ErgonomicEntityAwareCursorJobSystem(probe));
		world.AddSystem(new ErgonomicRegularCursorJobSystem(probe));

		world.Tick(1f / 60f);

		probe.MaxConcurrent.Should().Be(1);
	}

	[Fact]
	public void UpdateAll_WhenSystemsUseReadOnlyAndMutableQueryViewAccessOnSameManagedComponent_ShouldSerializeExecution()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var       probe = new ErgonomicConcurrencyProbe();

		world.Spawn(new ErgonomicReadOnlyNote { Label = "ready" });

		world.AddSystem(new ErgonomicReadOnlyQueryViewSystemA(probe));
		world.AddSystem(new ErgonomicMutableQueryViewSystem(probe));

		world.Tick(1f / 60f);

		probe.MaxConcurrent.Should().Be(1);
	}

	[Fact]
	public void UpdateAll_WhenSystemsUseReadOnlyQueryViewForEachOnSameManagedComponent_ShouldAllowConcurrentExecution()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var       probe = ErgonomicConcurrencyProbe.ForParallelAssertion();

		world.Spawn(new ErgonomicReadOnlyNote { Label = "ready" });

		world.AddSystem(new ErgonomicReadOnlyQueryViewSystemA(probe));
		world.AddSystem(new ErgonomicReadOnlyQueryViewSystemB(probe));

		world.Tick(1f / 60f);

		probe.MaxConcurrent.Should().Be(2);
	}

	[Fact]
	public void UpdateAll_WhenSystemsUseQueryViewRunParallelOnDisjointComponents_ShouldAllowConcurrentExecution()
	{
		using var world = new World(new WorldConfig { MaxDegreeOfParallelism = 4 });
		var       probe = ErgonomicConcurrencyProbe.ForParallelAssertion();

		world.Spawn(new ErgonomicParallelA { Value = 1 });
		world.Spawn(new ErgonomicParallelB { Value = 2 });

		world.AddSystem(new ErgonomicParallelQueryViewSystemA(probe));
		world.AddSystem(new ErgonomicParallelQueryViewSystemB(probe));

		world.Tick(1f / 60f);

		probe.MaxConcurrent.Should().Be(2);
	}
}

internal sealed class ErgonomicConcurrencyProbe(System.TimeSpan firstWorkerWait)
{
	private readonly System.Threading.ManualResetEventSlim _release = new(false);
	private          int                                   _maxConcurrent;
	private          int                                   _running;

	public int MaxConcurrent => System.Threading.Volatile.Read(ref _maxConcurrent);

	public ErgonomicConcurrencyProbe() : this(System.TimeSpan.FromMilliseconds(200)) { }

	public static ErgonomicConcurrencyProbe ForParallelAssertion() =>
		new(System.TimeSpan.FromSeconds(5));

	public void Enter()
	{
		int running = System.Threading.Interlocked.Increment(ref _running);
		UpdateMax(running);

		if (running == 1)
			_release.Wait(firstWorkerWait);
		else
			_release.Set();

		System.Threading.Interlocked.Decrement(ref _running);
	}

	private void UpdateMax(int candidate)
	{
		while (true)
		{
			int current = System.Threading.Volatile.Read(ref _maxConcurrent);
			if (candidate <= current)
				return;

			if (System.Threading.Interlocked.CompareExchange(ref _maxConcurrent, candidate, current) == current)
				return;
		}
	}
}

[ReadsResource(typeof(ErgonomicSchedulerResource))]
internal sealed class ErgonomicReadResourceSystem(ErgonomicConcurrencyProbe probe) : ISystem
{
	public void Update(in SystemContext context)
	{
		_ = context.World.ReadResource<ErgonomicSchedulerResource>();
		probe.Enter();
	}
}

internal sealed class ErgonomicSchedulerResource;

[ReadsResource(typeof(ErgonomicSchedulerResource))]
internal sealed class ErgonomicReadResourceSystemB(ErgonomicConcurrencyProbe probe) : ISystem
{
	public void Update(in SystemContext context)
	{
		_ = context.World.ReadResource<ErgonomicSchedulerResource>();
		probe.Enter();
	}
}

[WritesResource(typeof(ErgonomicSchedulerResource))]
internal sealed class ErgonomicWriteResourceSystem(ErgonomicConcurrencyProbe probe) : ISystem
{
	public void Update(in SystemContext context)
	{
		ref var resource = ref context.World.GetOrCreateResource<ErgonomicSchedulerResource>();
		probe.Enter();
	}
}

[Query]
[With(typeof(ErgonomicJobPosition))]
[With(typeof(ErgonomicJobVelocity))]
internal readonly partial struct ErgonomicJobQuery;

internal struct ErgonomicJobPosition
{
	public int Value;
}

internal struct ErgonomicJobVelocity
{
	public int Value;
}

internal readonly struct ErgonomicEntityAwareJob(ErgonomicConcurrencyProbe probe)
	: IForEachEntity<ErgonomicJobPosition, ErgonomicJobVelocity>
{
	public void Execute(Entity entity, ref ErgonomicJobPosition component1, in ErgonomicJobVelocity component2)
	{
		probe.Enter();
		component1.Value += component2.Value;
	}
}

internal readonly struct ErgonomicRegularJob(ErgonomicConcurrencyProbe probe)
	: IForEach<ErgonomicJobPosition, ErgonomicJobVelocity>
{
	public void Execute(ref ErgonomicJobPosition component1, in ErgonomicJobVelocity component2)
	{
		probe.Enter();
		component1.Value += component2.Value;
	}
}

[Reads(typeof(ErgonomicJobVelocity)), Writes(typeof(ErgonomicJobPosition))]
internal sealed class ErgonomicEntityAwareJobSystem(ErgonomicConcurrencyProbe probe) : ISystem
{
	public void Update(in SystemContext context)
	{
		context.World.Query<ErgonomicJobQuery>().Run(new ErgonomicEntityAwareJob(probe));
	}
}

[Reads(typeof(ErgonomicJobVelocity)), Writes(typeof(ErgonomicJobPosition))]
internal sealed class ErgonomicRegularJobSystem(ErgonomicConcurrencyProbe probe) : ISystem
{
	public void Update(in SystemContext context)
	{
		context.World.Query<ErgonomicJobQuery>().Run(new ErgonomicRegularJob(probe));
	}
}

[Reads(typeof(ErgonomicJobVelocity)), Writes(typeof(ErgonomicJobPosition))]
internal sealed class ErgonomicEntityAwareWorldJobSystem(ErgonomicConcurrencyProbe probe) : ISystem
{
	public void Update(in SystemContext context)
	{
		var handle = context.World.Compile<ErgonomicJobQuery>();
		context.World.Run(handle, new ErgonomicEntityAwareJob(probe));
	}
}

[Reads(typeof(ErgonomicJobVelocity)), Writes(typeof(ErgonomicJobPosition))]
internal sealed class ErgonomicRegularWorldJobSystem(ErgonomicConcurrencyProbe probe) : ISystem
{
	public void Update(in SystemContext context)
	{
		var handle = context.World.Compile<ErgonomicJobQuery>();
		context.World.Run(handle, new ErgonomicRegularJob(probe));
	}
}

[Reads(typeof(ErgonomicJobVelocity)), Writes(typeof(ErgonomicJobPosition))]
internal sealed class ErgonomicEntityAwareCursorJobSystem(ErgonomicConcurrencyProbe probe) : ISystem
{
	public void Update(in SystemContext context)
	{
		var handle = context.World.Compile<ErgonomicJobQuery>();
		using var cursor = context.World.Execute(handle);
		if (!cursor.MoveNext())
			return;

		cursor.Run(new ErgonomicEntityAwareJob(probe));
	}
}

[Reads(typeof(ErgonomicJobVelocity)), Writes(typeof(ErgonomicJobPosition))]
internal sealed class ErgonomicRegularCursorJobSystem(ErgonomicConcurrencyProbe probe) : ISystem
{
	public void Update(in SystemContext context)
	{
		var handle = context.World.Compile<ErgonomicJobQuery>();
		using var cursor = context.World.Execute(handle);
		if (!cursor.MoveNext())
			return;

		cursor.Run(new ErgonomicRegularJob(probe));
	}
}

[Query]
[With(typeof(ErgonomicReadOnlyNote))]
internal readonly partial struct ErgonomicReadOnlyNoteQuery;

internal struct ErgonomicReadOnlyNote
{
	public string Label;
}

[Reads(typeof(ErgonomicReadOnlyNote))]
internal sealed class ErgonomicReadOnlyQueryViewSystemA(ErgonomicConcurrencyProbe probe) : ISystem
{
	public void Update(in SystemContext context)
	{
		context.World.Query<ErgonomicReadOnlyNoteQuery>().ForEachRead<ErgonomicReadOnlyNote>(
			(Entity entity, in ErgonomicReadOnlyNote note) => probe.Enter()
		);
	}
}

[Reads(typeof(ErgonomicReadOnlyNote))]
internal sealed class ErgonomicReadOnlyQueryViewSystemB(ErgonomicConcurrencyProbe probe) : ISystem
{
	public void Update(in SystemContext context)
	{
		context.World.Query<ErgonomicReadOnlyNoteQuery>().ForEachRead<ErgonomicReadOnlyNote>(
			(Entity entity, in ErgonomicReadOnlyNote note) => probe.Enter()
		);
	}
}

[Writes(typeof(ErgonomicReadOnlyNote))]
internal sealed class ErgonomicMutableQueryViewSystem(ErgonomicConcurrencyProbe probe) : ISystem
{
	public void Update(in SystemContext context)
	{
		context.World.Query<ErgonomicReadOnlyNoteQuery>().ForEach<ErgonomicReadOnlyNote>(
			(Entity entity, ref ErgonomicReadOnlyNote note) =>
			{
				probe.Enter();
				note.Label += "!";
			}
		);
	}
}

[Query]
[With(typeof(ErgonomicParallelA))]
internal readonly partial struct ErgonomicParallelQueryA;

[Query]
[With(typeof(ErgonomicParallelB))]
internal readonly partial struct ErgonomicParallelQueryB;

internal struct ErgonomicParallelA
{
	public int Value;
}

internal struct ErgonomicParallelB
{
	public int Value;
}

internal readonly struct ErgonomicParallelJobA(ErgonomicConcurrencyProbe probe) : IForEach<ErgonomicParallelA>
{
	public void Execute(ref ErgonomicParallelA component1)
	{
		probe.Enter();
		component1.Value++;
	}
}

internal readonly struct ErgonomicParallelJobB(ErgonomicConcurrencyProbe probe) : IForEach<ErgonomicParallelB>
{
	public void Execute(ref ErgonomicParallelB component1)
	{
		probe.Enter();
		component1.Value++;
	}
}

[Writes(typeof(ErgonomicParallelA))]
internal sealed class ErgonomicParallelQueryViewSystemA(ErgonomicConcurrencyProbe probe) : ISystem
{
	public void Update(in SystemContext context)
	{
		var handle = context.World.Compile<ErgonomicParallelQueryA>();
		var query  = new QueryView<ErgonomicParallelQueryA>(context.World, handle);
		query.RunParallel<ErgonomicParallelJobA, ErgonomicParallelA>(new(probe), 2);
	}
}

[Writes(typeof(ErgonomicParallelB))]
internal sealed class ErgonomicParallelQueryViewSystemB(ErgonomicConcurrencyProbe probe) : ISystem
{
	public void Update(in SystemContext context)
	{
		var handle = context.World.Compile<ErgonomicParallelQueryB>();
		var query  = new QueryView<ErgonomicParallelQueryB>(context.World, handle);
		query.RunParallel<ErgonomicParallelJobB, ErgonomicParallelB>(new(probe), 2);
	}
}

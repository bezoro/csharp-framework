using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.ECS.Internal;
using FluentAssertions;
using Xunit;

namespace Bezoro.ECS.Tests.Internal;

public sealed class WorldResourceLifetimeTests
{
	[Theory]
	[InlineData("factory")]
	[InlineData("replace")]
	[InlineData("remove")]
	[InlineData("clear")]
	public void ResourceCallbacks_DoNotHoldTheStoreGateWhileAnotherThreadReads(string operation)
	{
		var store = new WorldResourceStore();
		store.Set(new Counter { Value = 17 });
		using var readFinished = new ManualResetEventSlim();
		Exception? readFailure = null;
		Thread? reader = null;
		void ReadFromAnotherThread()
		{
			reader = new Thread(() =>
			{
				try { _ = store.Has<Counter>(); }
				catch (Exception failure) { readFailure = failure; }
				finally { readFinished.Set(); }
			}) { IsBackground = true };
			reader.Start();
			Assert.True(readFinished.Wait(TimeSpan.FromSeconds(2)), "A resource callback blocked an independent reader.");
		}

		try
		{
			if (operation == "factory")
				store.GetOrCreate(() => { ReadFromAnotherThread(); return new DisposableResource(); });
			else
			{
				store.Set(new CallbackResource(ReadFromAnotherThread));
				if (operation == "replace") store.Set(new CallbackResource(() => { }));
				else if (operation == "remove") store.Remove<CallbackResource>();
				else store.Clear();
			}
		}
		finally { if (reader is not null) Assert.True(reader.Join(TimeSpan.FromSeconds(10))); }
		Assert.Null(readFailure);
		store.Dispose();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ReplacingWithSameInstance_RetainsLiveResource(bool boxed)
	{
		var store = new WorldResourceStore();
		var resource = new DisposableResource();
		store.Set(resource);
		if (boxed) store.SetBoxed(typeof(DisposableResource), resource);
		else store.Set(resource);

		resource.DisposeCount.Should().Be(0);
		store.Get<DisposableResource>().Should().BeSameAs(resource);
		store.Dispose();
		resource.DisposeCount.Should().Be(1);
	}

	[Fact]
	public void Dispose_DetachesAndDisposesEachOwnedResourceOnce()
	{
		var store = new WorldResourceStore();
		var resource = new DisposableResource();
		store.Set(resource);
		store.Dispose();
		store.Dispose();

		store.Count.Should().Be(0);
		resource.DisposeCount.Should().Be(1);
	}

	[Fact]
	public void Clear_AttemptsAllDisposalsAndDetachesFailedResource()
	{
		var store = new WorldResourceStore();
		var healthy = new DisposableResource();
		store.Set(new ThrowingDisposable());
		store.Set(healthy);
		Action clear = store.Clear;

		clear.Should().Throw<AggregateException>();
		healthy.DisposeCount.Should().Be(1);
		store.Count.Should().Be(0);
		store.Clear();
	}

	[Fact]
	public void RestoredStructResource_ProvidesStableWritableTypedReference()
	{
		var store = new WorldResourceStore();
		store.SetBoxed(typeof(Counter), new Counter { Value = 12 });
		ref Counter counter = ref store.Get<Counter>();
		counter.Value = 23;

		store.TryRead<Counter>(out var read).Should().BeTrue();
		read.Value.Should().Be(23);
		store.GetOrCreate(() => new Counter()).Value.Should().Be(23);
		store.GetOrCreate<Counter>().Value.Should().Be(23);
	}

	[Fact]
	public void ResourceFactoryReturningNull_DoesNotRegisterInvalidResource()
	{
		var store = new WorldResourceStore();
		Action create = () => store.GetOrCreate<DisposableResource>(() => null!);
		create.Should().Throw<InvalidOperationException>();
		store.Count.Should().Be(0);
	}

	[Fact]
	public void ResourceFactory_CannotOverwriteAReentrantRegistration()
	{
		var store = new WorldResourceStore();
		var resource = new DisposableResource();
		Action create = () => store.GetOrCreate(() =>
		{
			store.Set(resource);
			return new DisposableResource();
		});
		create.Should().Throw<InvalidOperationException>().WithMessage("*cannot mutate*");
		store.Count.Should().Be(0);
		resource.DisposeCount.Should().Be(0, "the rejected registration remains caller-owned");
		resource.Dispose();
	}

	[Fact]
	public void DisposalCallback_CannotRepopulateTheStoreBeingCleared()
	{
		var store = new WorldResourceStore();
		var resource = new DisposableResource();
		store.Set(new CallbackResource(() => store.Set(resource)));
		Action clear = store.Clear;
		clear.Should().Throw<AggregateException>();
		store.Count.Should().Be(0);
		resource.DisposeCount.Should().Be(0);
		resource.Dispose();
	}

	[Fact]
	public void RestoredResource_ParallelFirstAccessKeepsOneStableBox()
	{
		var store = new WorldResourceStore();
		store.SetBoxed(typeof(Counter), new Counter());
		Parallel.For(0, 10_000, _ => Interlocked.Increment(ref store.Get<Counter>().Value));
		store.Get<Counter>().Value.Should().Be(10_000);
	}

	private sealed class CallbackResource(Action callback) : IDisposable
	{
		public void Dispose() => callback();
	}

	[Fact]
	public void RestoredStructResource_RepeatedTypedReadsDoNotBox()
	{
		const int readCount = 10_000;
		const int sampleCount = 8;
		var store = new WorldResourceStore();
		store.SetBoxed(typeof(Counter), new Counter { Value = 1 });

		// Warm the measured helper and the complete read path, including materialization.
		MeasureTypedReadAllocations(store, readCount, out _);
		var samples = new long[sampleCount];
		long minimumAllocated = long.MaxValue;
		for (int sample = 0; sample < sampleCount; sample++)
		{
			samples[sample] = MeasureTypedReadAllocations(store, readCount, out int sum);
			sum.Should().Be(readCount);
			minimumAllocated = Math.Min(minimumAllocated, samples[sample]);
		}

		// Runtime initialization can affect a batch, but boxing on every read affects
		// every batch. Require an allocation-free batch from this fixed sample window.
		minimumAllocated.Should().Be(0,
			"steady-state typed reads must not box; allocated bytes per batch: {0}", string.Join(", ", samples));
	}

	[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
	private static long MeasureTypedReadAllocations(WorldResourceStore store, int readCount, out int sum)
	{
		// Keep JIT inlining/OSR of the measurement loop outside the allocation contract.
		sum = 0;
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int iteration = 0; iteration < readCount; iteration++)
		{
			store.TryRead<Counter>(out var counter);
			sum += counter.Value;
		}
		return GC.GetAllocatedBytesForCurrentThread() - before;
	}

	private struct Counter { public int Value; }
	private sealed class DisposableResource : IDisposable
	{
		public int DisposeCount { get; private set; }
		public void Dispose() => DisposeCount++;
	}
	private sealed class ThrowingDisposable : IDisposable
	{
		public void Dispose() => throw new InvalidOperationException("Dispose failed.");
	}
}

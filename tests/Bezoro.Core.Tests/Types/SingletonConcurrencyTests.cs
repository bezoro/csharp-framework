using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.Core.Types;
using Xunit;

namespace Bezoro.Core.Tests.Types;

public sealed class SingletonConcurrencyTests
{
	[Fact]
	public async Task FactoryCallback_CanJoinAWorkerWhoseLifecycleChangeIsRejected()
	{
		Singleton<FactoryOwner>.ResetAndDispose();
		Task<Exception>? worker = null;
		bool completedInsideFactory = false;
		Singleton<FactoryOwner>.ConfigureFactory(() =>
		{
			worker = Task.Run(() => Record.Exception(() => Singleton<FactoryOwner>.ConfigureFactory(() => new FactoryOwner())));
			completedInsideFactory = worker.Wait(TimeSpan.FromSeconds(5));
			return new FactoryOwner();
		});
		try
		{
			var value = Singleton<FactoryOwner>.Instance;
			Assert.IsType<InvalidOperationException>(await worker!.WaitAsync(TimeSpan.FromSeconds(5)));
			Assert.True(completedInsideFactory);
			Assert.Same(value, Singleton<FactoryOwner>.Instance);
		}
		finally { Singleton<FactoryOwner>.ResetAndDispose(); }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DisposalCallback_CanJoinAWorkerWithoutHoldingTheLifecycleMonitor(bool scoped)
	{
		Singleton<DisposalOwner>.ResetAndDispose();
		var scope = scoped ? Singleton<DisposalOwner>.Override(() => new DisposalOwner()) : null;
		var value = Singleton<DisposalOwner>.Instance;
		try
		{
			if (scope is null) Singleton<DisposalOwner>.ResetAndDispose();
			else scope.Dispose();
			Assert.IsType<InvalidOperationException>(await value.Worker!.WaitAsync(TimeSpan.FromSeconds(5)));
			Assert.True(value.CompletedInsideDispose);
			Assert.False(Singleton<DisposalOwner>.IsValueCreated);
		}
		finally { Singleton<DisposalOwner>.ResetAndDispose(); }
	}

	[Fact]
	public async Task ConcurrentReaders_JoinOnePendingFactoryAndItsCachedFailure()
	{
		Singleton<FailedOwner>.ResetAndDispose();
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		int calls = 0;
		var failure = new InvalidOperationException("expected factory failure");
		Singleton<FailedOwner>.ConfigureFactory(() =>
		{
			Interlocked.Increment(ref calls);
			entered.Set();
			if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
			throw failure;
		});
		var first = Task.Run(() => Record.Exception(() => _ = Singleton<FailedOwner>.Instance));
		try
		{
			Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
			var readers = Enumerable.Range(0, 8).Select(reader => Task.Run(() => Record.Exception(() => _ = Singleton<FailedOwner>.Instance))).ToArray();
			release.Set();
			Assert.Same(failure, await first.WaitAsync(TimeSpan.FromSeconds(5)));
			foreach (var error in await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5))) Assert.Same(failure, error);
			Assert.Equal(1, calls);
		}
		finally { release.Set(); Singleton<FailedOwner>.ResetAndDispose(); }
	}

	[Fact]
	public void RecursiveFactoryRead_RejectsInsteadOfPublishingAPartialObject()
	{
		Singleton<RecursiveOwner>.ResetAndDispose();
		Singleton<RecursiveOwner>.ConfigureFactory(() => Singleton<RecursiveOwner>.Instance);
		try
		{
			Assert.Throws<InvalidOperationException>(() => _ = Singleton<RecursiveOwner>.Instance);
			Assert.False(Singleton<RecursiveOwner>.TryGet(out _));
		}
		finally { Singleton<RecursiveOwner>.ResetAndDispose(); }
	}

	private sealed class FactoryOwner : Singleton<FactoryOwner>;
	private sealed class FailedOwner : Singleton<FailedOwner>;
	private sealed class RecursiveOwner : Singleton<RecursiveOwner>;
	private sealed class DisposalOwner : Singleton<DisposalOwner>, IDisposable
	{
		internal Task<Exception>? Worker;
		internal bool CompletedInsideDispose;
		public void Dispose()
		{
			Worker = Task.Run(() => Record.Exception(() => Singleton<DisposalOwner>.Reset()));
			CompletedInsideDispose = Worker.Wait(TimeSpan.FromSeconds(5));
		}
	}
}

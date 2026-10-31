using System;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.Core.Types;
using Xunit;

namespace Bezoro.Core.Tests.Types;

public class SingletonLifecycleTests
{
	[Fact]
	public async Task FactoryGuardDoesNotPermitConstructionOnAnotherThread()
	{
		Singleton<OwnedSingleton>.ResetAndDispose();
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		var creation = Task.Run(() => Singleton<OwnedSingleton>.Override(() =>
		{
			entered.Set();
			if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
			return new OwnedSingleton();
		}));
		try
		{
			Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
			Assert.Throws<InvalidOperationException>(() => new OwnedSingleton());
		}
		finally
		{
			release.Set();
			using var scope = await creation;
		}
	}

	[Fact]
	public void OverrideScopesRejectOutOfOrderDisposalWithoutLosingOwnership()
	{
		Singleton<ScopedSingleton>.ResetAndDispose();
		var outer = Singleton<ScopedSingleton>.Override(() => new ScopedSingleton());
		var first = Singleton<ScopedSingleton>.Instance;
		var inner = Singleton<ScopedSingleton>.Override(() => new ScopedSingleton());
		var second = Singleton<ScopedSingleton>.Instance;
		try
		{
			Assert.Throws<InvalidOperationException>(() => outer.Dispose());
			Assert.Same(second, Singleton<ScopedSingleton>.Instance);
			inner.Dispose();
			Assert.Same(first, Singleton<ScopedSingleton>.Instance);
			outer.Dispose();
			Assert.Equal(1, first.Disposals);
			Assert.Equal(1, second.Disposals);
		}
		finally
		{
			Singleton<ScopedSingleton>.ResetAndDispose();
		}
	}

	[Fact]
	public void ResetDisposesEveryOwnedInstanceAndInvalidatesOutstandingScopes()
	{
		Singleton<ResetSingleton>.ResetAndDispose();
		var original = Singleton<ResetSingleton>.Instance;
		var outer = Singleton<ResetSingleton>.Override(() => new ResetSingleton());
		var first = Singleton<ResetSingleton>.Instance;
		var inner = Singleton<ResetSingleton>.Override(() => new ResetSingleton());
		var second = Singleton<ResetSingleton>.Instance;
		Singleton<ResetSingleton>.ResetAndDispose();
		inner.Dispose();
		outer.Dispose();
		Assert.Equal(1, original.Disposals);
		Assert.Equal(1, first.Disposals);
		Assert.Equal(1, second.Disposals);
		Assert.False(Singleton<ResetSingleton>.TryGet(out _));
	}

	[Fact]
	public void OverrideCannotTakeOwnershipOfAnAlreadyOwnedInstance()
	{
		Singleton<DuplicateSingleton>.ResetAndDispose();
		var original = Singleton<DuplicateSingleton>.Instance;
		Assert.Throws<InvalidOperationException>(() => Singleton<DuplicateSingleton>.Override(() => original));
		Assert.Same(original, Singleton<DuplicateSingleton>.Instance);
		Assert.Equal(0, original.Disposals);
		Singleton<DuplicateSingleton>.ResetAndDispose();
	}

	private sealed class OwnedSingleton : Singleton<OwnedSingleton>;
	private sealed class ScopedSingleton : Singleton<ScopedSingleton>, IDisposable
	{
		public int Disposals;
		public void Dispose() => Disposals++;
	}
	private sealed class ResetSingleton : Singleton<ResetSingleton>, IDisposable
	{
		public int Disposals;
		public void Dispose() => Disposals++;
	}
	private sealed class DuplicateSingleton : Singleton<DuplicateSingleton>, IDisposable
	{
		public int Disposals;
		public void Dispose() => Disposals++;
	}
}

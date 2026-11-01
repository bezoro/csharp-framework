using Bezoro.ECS.Services;
using Bezoro.GameSystems.ActivationSystem.Extensions;
using Bezoro.GameSystems.ActivationSystem.Types;
using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace Bezoro.GameSystems.Tests.ActivationSystem;

public sealed class ActivationQueueIdentityTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void TerminalEntry_DoesNotRetainCallbackTarget(bool cancel)
	{
		using var world = new World();
		world.AddActivationPipeline();
		var reference = RegisterTarget(world.GetOrCreateActivationCommandQueue(), cancel);
		world.Tick(0f);
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		Assert.False(reference.IsAlive);
		GC.KeepAlive(world);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static WeakReference RegisterTarget(ActivationCommandQueue queue, bool cancel)
	{
		var target = new object();
		var handle = queue.Register(() => GC.KeepAlive(target));
		if (cancel) queue.Cancel(handle);
		return new WeakReference(target);
	}

	[Fact]
	public void HandleFromAnotherQueue_CannotCancelLocalActivation()
	{
		using var world = new World();
		world.AddActivationPipeline();
		var foreign = new ActivationCommandQueue().Register(() => { });
		var localQueue = world.GetOrCreateActivationCommandQueue();
		var calls = 0;
		var local = localQueue.Register(() => calls++);
		localQueue.Cancel(foreign);
		world.Tick(0f);
		Assert.Equal(1, calls);
		Assert.NotEqual(foreign, local);
	}
}

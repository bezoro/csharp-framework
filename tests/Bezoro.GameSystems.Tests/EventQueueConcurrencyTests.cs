using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.Core.Types;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Bezoro.GameSystems.ActivationSystem.Services;
using Bezoro.GameSystems.ActivationSystem.Extensions;
using Bezoro.GameSystems.ActivationSystem.Types;
using Bezoro.GameSystems.HealthSystem.Types;
using Bezoro.GameSystems.StreamingSystem.Types;
using Bezoro.GameSystems.TimerSystem.Types;
using Xunit;

namespace Bezoro.GameSystems.Tests;

public sealed class EventQueueConcurrencyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ConcurrentProducersAndConsumersDoNotLoseOrRepeatEvents(int kind)
    {
        const int workers = 4;
        const int perWorker = 500;
        var queue = Create(kind, workers * perWorker, workers * perWorker);
        Parallel.For(0, workers, worker =>
        {
            for (int i = 0; i < perWorker; i++) queue.Enqueue(worker * perWorker + i);
        });
        var received = new ConcurrentBag<int>();
        Parallel.For(0, workers, _ =>
        {
            while (true)
            {
                var item = queue.TryDequeue();
                if (!item.Found) break;
                received.Add(item.Value);
            }
        });
        Assert.Equal(Enumerable.Range(0, workers * perWorker), received.Order());
        Assert.Equal(0, queue.Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void BoundedFailurePreservesPendingEventsAndClearRetainsCapacity(int kind)
    {
        var queue = Create(kind, 2, 2);
        queue.Enqueue(10);
        Assert.True(queue.TryEnqueue(20));
        Assert.False(queue.TryEnqueue(30));
        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(30));
        Assert.Equal((true, 10), queue.TryDequeue());
        Assert.Equal((true, 20), queue.TryDequeue());
        Assert.Equal((false, 0), queue.TryDequeue());
        queue.Enqueue(40);
        queue.Clear();
        Assert.True(queue.TryEnqueue(50));
        Assert.Equal((true, 50), queue.TryDequeue());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WarmEventStorageDoesNotAllocatePerOperation(int kind)
    {
        var queue = Create(kind, 1, 1);
        for (int i = 0; i < 100; i++) { queue.Enqueue(i); queue.TryDequeue(); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { queue.Enqueue(i); queue.TryDequeue(); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void DispatchProcessesOneSnapshotAndDefersCallbacksPostedByCallbacks()
    {
        using var world = new World();
        world.AddSystem(new ActivationDispatchSystem(), Stage.PostTick);
        var queue = world.ReadResource<ActivationDispatchQueueResource>();
        int calls = 0;
        Action? repost = null;
        repost = () => { calls++; queue.Enqueue(repost!); };
        queue.Enqueue(repost);
        world.Tick(0f);
        Assert.Equal(1, calls);
        Assert.Equal(1, queue.Count);
        world.Tick(0f);
        Assert.Equal(2, calls);
        queue.Clear();
    }

    [Fact]
    public void CallbackStorageEnforcesBackpressureAndNeverInvokesWhileQueueing()
    {
        var queue = new ActivationDispatchQueueResource(initialCapacity: 1, maxCapacity: 1);
        int calls = 0;
        Action callback = () => calls++;
        Assert.True(queue.TryEnqueue(callback));
        Assert.False(queue.TryEnqueue(callback));
        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(callback));
        Assert.Equal(0, calls);
        Assert.True(queue.TryDequeue(out var item));
        item();
        Assert.Equal(1, calls);
        Assert.False(queue.TryDequeue(out _));
        Assert.Throws<ArgumentNullException>(() => queue.TryEnqueue(null!));
    }

    [Fact]
    public void AFullDispatchQueueLeavesCallbacksPendingUntilALaterUpdate()
    {
        using var world = new World();
        world.SetResource(new ActivationDispatchQueueResource(initialCapacity: 1, maxCapacity: 1));
        world.AddActivationPipeline();
        var queue = world.GetOrCreateActivationCommandQueue();
        int calls = 0;
        queue.Register(() => calls++);
        queue.Register(() => calls++);
        queue.Register(() => calls++);
        world.Tick(0f);
        Assert.Equal(1, calls);
        world.Tick(0f);
        Assert.Equal(2, calls);
        world.Tick(0f);
        Assert.Equal(3, calls);
        Assert.Equal(0, world.ReadResource<ActivationRuntimeState>().PendingCount);
    }

    private static QueueDriver Create(int kind, int initial, int max)
    {
        switch (kind)
        {
            case 0:
                var activation = new ActivationEventsResource(initial, max);
                return new(value => activation.Enqueue(new(value)), value => activation.TryEnqueue(new(value)),
                    () => activation.TryDequeue(out var item) ? (true, item.ActivatedCount) : (false, 0),
                    () => activation.Count, activation.Clear);
            case 1:
                var health = new HealthEventsResource(initial, max);
                return new(value => health.Enqueue(Health(value)), value => health.TryEnqueue(Health(value)),
                    () => health.TryDequeue(out var item) ? (true, (int)item.Value) : (false, 0),
                    () => health.Count, health.Clear);
            case 2:
                var streaming = new StreamingEventsResource(initial, max);
                return new(value => streaming.Enqueue(new(default, StreamingTransition.StreamedIn, value)),
                    value => streaming.TryEnqueue(new(default, StreamingTransition.StreamedIn, value)),
                    () => streaming.TryDequeue(out var item) ? (true, (int)item.DistanceSquared) : (false, 0),
                    () => streaming.Count, streaming.Clear);
            default:
                var timer = new TimerEventsResource(initial, max);
                return new(value => timer.Enqueue(Timer(value)), value => timer.TryEnqueue(Timer(value)),
                    () => timer.TryDequeue(out var item) ? (true, item.TimerId) : (false, 0),
                    () => timer.Count, timer.Clear);
        }
    }

    private static HealthChangedEvent Health(int value) =>
        new(default, HealthChangeKind.Damage, (uint)value, MaxValueUpdateMode.ClampCurrent, 0, 0, 0, 0, 0, 0);
    private static TimerLifecycleEvent Timer(int value) => new(default, default, value, default, default, 0, 0);

    private sealed class QueueDriver(Action<int> enqueue, Func<int, bool> tryEnqueue,
        Func<(bool Found, int Value)> tryDequeue, Func<int> count, Action clear)
    {
        internal Action<int> Enqueue { get; } = enqueue;
        internal Func<int, bool> TryEnqueue { get; } = tryEnqueue;
        internal Func<(bool Found, int Value)> TryDequeue { get; } = tryDequeue;
        internal Func<int> Count { get; } = count;
        internal Action Clear { get; } = clear;
    }
}

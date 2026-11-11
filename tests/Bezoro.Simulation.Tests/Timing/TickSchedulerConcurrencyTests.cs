using System.Collections.Concurrent;
using Bezoro.Simulation.Timing;
using Xunit;

namespace Bezoro.Simulation.Tests.Timing;

public sealed class TickSchedulerConcurrencyTests
{
    [Fact]
    public void ConcurrentProducersAndConsumersPreserveEveryPayloadExactlyOnce()
    {
        const int workers = 4;
        const int perWorker = 1000;
        var scheduler = new TickScheduler<int>(workers * perWorker);
        using var start = new Barrier(workers);
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
        {
            start.SignalAndWait();
            for (int i = 0; i < perWorker; i++) scheduler.Schedule(worker * perWorker + i, 0);
        });

        Assert.Equal(workers * perWorker, scheduler.Count);
        var received = new ConcurrentBag<int>();
        Parallel.For(0, workers, worker =>
        {
            var buffer = new ScheduledItem<int>[17];
            while (true)
            {
                var drained = scheduler.DrainDue(buffer);
                for (int i = 0; i < drained.Count; i++) received.Add(buffer[i].Value);
                if (!drained.HasMoreDue) break;
            }
        });
        Assert.Equal(Enumerable.Range(0, workers * perWorker), received.Order());
        Assert.Equal(0, scheduler.Count);
        Assert.Equal(scheduler.Capacity, scheduler.RemainingCapacity);
    }

    [Fact]
    public void ConcurrentCancellationOfOneHandleReleasesItsCapacityOnce()
    {
        var scheduler = new TickScheduler<int>(1);
        var handle = scheduler.Schedule(42, 0);
        int accepted = 0;
        Parallel.For(0, 64, _ =>
        {
            if (scheduler.TryCancel(handle, out int value))
            {
                Assert.Equal(42, value);
                Interlocked.Increment(ref accepted);
            }
        });
        Assert.Equal(1, accepted);
        Assert.Equal(1, scheduler.RemainingCapacity);
        Assert.False(scheduler.Contains(handle));
    }

    [Fact]
    public async Task RelativeSchedulingIsAtomicWithClockAdvancement()
    {
        const int iterations = 10000;
        var scheduler = new TickScheduler<int>(iterations);
        using var start = new Barrier(2);
        var producer = Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < iterations; i++) scheduler.Schedule(i, 0);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        start.SignalAndWait();
        for (int tick = 0; tick < iterations; tick++)
            scheduler.AdvanceTo(tick, Span<ScheduledItem<int>>.Empty);
        await producer;
        var items = new ScheduledItem<int>[iterations];
        Assert.Equal(iterations, scheduler.DrainDue(items).Count);
        Assert.Equal(Enumerable.Range(0, iterations), items.Select(item => item.Value));
    }
}

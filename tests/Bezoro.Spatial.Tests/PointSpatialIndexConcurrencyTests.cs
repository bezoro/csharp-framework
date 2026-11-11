using System.Numerics;
using Bezoro.Core.Collections;
using Xunit;

namespace Bezoro.Spatial.Tests;

public sealed class PointSpatialIndexConcurrencyTests
{
    [Fact]
    public void ConcurrentAddsAndRemovalsPreserveEveryHandleAndBucket()
    {
        const int workers = 4;
        const int perWorker = 250;
        var bounds = new Bounds2D(Vector2.Zero, new Vector2(workers * perWorker));
        var index = new PointSpatialIndex<int>(bounds, 50, workers * perWorker, 400);
        var handles = new SlotHandle[workers * perWorker];
        using var start = new Barrier(workers);
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
        {
            start.SignalAndWait();
            for (int i = 0; i < perWorker; i++)
            {
                int value = worker * perWorker + i;
                handles[value] = index.Add(new Vector2(value), value);
            }
        });
        Assert.Equal(handles.Length, handles.Distinct().Count());
        var hits = new SpatialHit<int>[handles.Length];
        Assert.Equal(handles.Length, index.QueryRectangle(bounds, hits).WrittenCount);
        Assert.Equal(Enumerable.Range(0, handles.Length), hits.Select(hit => hit.Value).Order());
        Parallel.For(0, handles.Length, i => Assert.True(index.Remove(handles[i])));
        Assert.Equal(0, index.Count);
        Assert.Equal(index.Capacity, index.AvailableCapacity);
    }

    [Fact]
    public async Task QueriesRemainConsistentWhileAnotherThreadMovesAddsAndRemovesPoints()
    {
        const int capacity = 64;
        var bounds = new Bounds2D(new Vector2(-100), new Vector2(100));
        var index = new PointSpatialIndex<int>(bounds, 10, capacity, 400);
        var handles = new SlotHandle[capacity];
        for (int i = 0; i < capacity; i++) handles[i] = index.Add(Vector2.Zero, i);
        using var start = new Barrier(2);
        var writer = Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < 4000; i++)
            {
                int value = i % capacity;
                Assert.True(index.TryMove(handles[value], new Vector2(i % 100)));
                Assert.True(index.Remove(handles[value]));
                handles[value] = index.Add(Vector2.Zero, value);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        start.SignalAndWait();
        var hits = new SpatialHit<int>[capacity];
        var shortBuffer = new SpatialHit<int>[1];
        try
        {
            for (int i = 0; i < 1000; i++)
            {
                var result = index.QueryRectangle(bounds, hits);
                Assert.InRange(result.WrittenCount, capacity - 1, capacity);
                Assert.Equal(result.WrittenCount, hits.Take(result.WrittenCount).Select(hit => hit.Value).Distinct().Count());
                var small = index.QueryRectangle(bounds, shortBuffer);
                Assert.Equal(SpatialQueryStatus.DestinationTooSmall, small.Status);
                Assert.InRange(small.RequiredCapacity, capacity - 1, capacity);
            }
        }
        finally { await writer; }
        Assert.Equal(capacity, index.Count);
    }

    [Fact]
    public void ValidationExceptionsReleaseReadAndWriteOwnership()
    {
        var bounds = new Bounds2D(Vector2.Zero, Vector2.One);
        var index = new PointSpatialIndex<int>(bounds, 1, 1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => index.QueryCircle(Vector2.Zero, -1, Array.Empty<SpatialHit<int>>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.TryAdd(new Vector2(float.NaN), 1, out _));
        var handle = index.Add(Vector2.Zero, 1);
        var hits = new SpatialHit<int>[1];
        Assert.Equal(1, index.QueryRectangle(bounds, hits).WrittenCount);
        Assert.True(index.Remove(handle));
    }
}

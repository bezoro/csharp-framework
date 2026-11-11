using System;
using System.Collections.Generic;
using System.Threading;
using Bezoro.Core.Types;
using Bezoro.Core.Types.Pool;
using Xunit;

namespace Bezoro.Core.Tests.Types.Pool;

public sealed class ObjectPoolNativeResourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TeardownReleasesAvailableNonDisposableResourcesThroughTheirPolicy(bool dispose)
    {
        var resources = new List<NativeResource>();
        using var pool = CreatePool(resources);
        if (dispose) pool.Dispose();
        else pool.Clear();
        Assert.Equal(3, resources.Count);
        Assert.All(resources, resource => Assert.Equal(1, resource.ReleaseCount));
        Assert.Equal(0, pool.AvailableCount);
        Assert.Equal(0, pool.TotalCount);
        Assert.Equal(3, pool.Statistics.TotalDiscarded);
        pool.Dispose();
        Assert.All(resources, resource => Assert.Equal(1, resource.ReleaseCount));
    }

    [Fact]
    public void DisposalPreservesOutstandingRentalUntilItsSingleLateReturn()
    {
        var resources = new List<NativeResource>();
        var pool = CreatePool(resources);
        var outstanding = pool.Rent();
        pool.Dispose();
        foreach (var resource in resources)
            Assert.Equal(ReferenceEquals(resource, outstanding) ? 0 : 1, resource.ReleaseCount);
        Assert.Equal(1, pool.TotalCount);
        Assert.False(pool.Return(outstanding));
        Assert.False(pool.Return(outstanding));
        Assert.All(resources, resource => Assert.Equal(1, resource.ReleaseCount));
        Assert.Equal(0, pool.TotalCount);
        Assert.Equal(3, pool.Statistics.TotalDiscarded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowingReleaseStillAttemptsEveryAvailableResourceAndNeverRetriesIt(bool dispose)
    {
        var resources = new List<NativeResource>();
        var pool = CreatePool(resources, failRelease: true);
        Assert.Throws<InvalidOperationException>(() =>
        {
            if (dispose) pool.Dispose();
            else pool.Clear();
        });
        Assert.All(resources, resource => Assert.Equal(1, resource.ReleaseCount));
        Assert.Equal(0, pool.TotalCount);
        Assert.Equal(0, pool.AvailableCount);
        Assert.Equal(3, pool.Statistics.TotalDiscarded);
        pool.Dispose();
        Assert.All(resources, resource => Assert.Equal(1, resource.ReleaseCount));
    }

    [Fact]
    public void FailedPrewarmingReleasesNativeResourcesAlreadyCreated()
    {
        var resources = new List<NativeResource>();
        var policy = new PoolPolicy<NativeResource>(() =>
        {
            if (resources.Count == 2) throw new InvalidOperationException("Factory failed.");
            var resource = new NativeResource();
            resources.Add(resource);
            return resource;
        }, onDiscard: resource => resource.Release());
        var error = Assert.Throws<InvalidOperationException>(() => new ObjectPool<NativeResource>(policy,
            new PoolOptions(InitialCapacity: 3, MaxCapacity: 3)));
        Assert.Equal("Factory failed.", error.Message);
        Assert.Equal(2, resources.Count);
        Assert.All(resources, resource => Assert.Equal(1, resource.ReleaseCount));
    }

    [Fact]
    public void ClearReleasesItsInitialSnapshotAndRetainsObjectsReturnedByReleaseCallbacks()
    {
        ObjectPool<NativeResource>? pool = null;
        NativeResource? replacement = null;
        bool replace = true;
        var policy = new PoolPolicy<NativeResource>(() => new NativeResource(), onDiscard: resource =>
        {
            resource.Release();
            if (!replace) return;
            replace = false;
            replacement = pool!.Rent();
            Assert.True(pool.Return(replacement));
        });
        using (pool = new ObjectPool<NativeResource>(policy, new PoolOptions(InitialCapacity: 1, MaxCapacity: 2)))
        {
            var original = pool.Rent();
            Assert.True(pool.Return(original));
            pool.Clear();
            Assert.Equal(1, original.ReleaseCount);
            Assert.NotNull(replacement);
            Assert.Equal(0, replacement.ReleaseCount);
            Assert.Equal(1, pool.TotalCount);
            Assert.Equal(1, pool.AvailableCount);
        }
        Assert.Equal(1, replacement!.ReleaseCount);
    }

    private static ObjectPool<NativeResource> CreatePool(List<NativeResource> resources, bool failRelease = false) =>
        new(new PoolPolicy<NativeResource>(() =>
        {
            var resource = new NativeResource();
            resources.Add(resource);
            return resource;
        }, onDiscard: resource =>
        {
            resource.Release();
            if (failRelease) throw new InvalidOperationException("Native release failed.");
        }), new PoolOptions(InitialCapacity: 3, MaxCapacity: 3, TrackStatistics: true));

    // Models an engine handle whose native release is deliberately not IDisposable.Dispose.
    private sealed class NativeResource
    {
        private int _releaseCount;
        internal int ReleaseCount => Volatile.Read(ref _releaseCount);
        internal void Release() => Interlocked.Increment(ref _releaseCount);
    }
}

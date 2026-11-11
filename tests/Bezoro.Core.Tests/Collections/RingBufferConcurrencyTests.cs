using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bezoro.Core.Collections;
using Xunit;

namespace Bezoro.Core.Tests.Collections;

public sealed class RingBufferConcurrencyTests
{
    [Fact]
    public async Task ConcurrentProducersPreserveEveryValueExactlyOnce()
    {
        const int workers = 8, perWorker = 5_000;
        var buffer = new RingBuffer<int>(workers * perWorker);
        using var start = new Barrier(workers);
        var producers = Enumerable.Range(0, workers).Select(worker => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (int index = 0; index < perWorker; index++)
                buffer.Enqueue(worker * perWorker + index);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(workers * perWorker, buffer.Count);
        var values = new int[buffer.Capacity];
        Assert.Equal(values.Length, buffer.CopyTo(values));
        Array.Sort(values);
        Assert.Equal(Enumerable.Range(0, values.Length), values);
    }

    [Fact]
    public async Task ConcurrentProducersAndConsumersRespectCapacityAndOwnership()
    {
        const int workers = 4, perWorker = 2_000;
        var buffer = new RingBuffer<int>(31);
        var seen = new int[workers * perWorker];
        int finished = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var consumers = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            var spin = new SpinWait();
            while (Volatile.Read(ref finished) != workers || buffer.Count != 0)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (buffer.TryDequeue(out int value))
                {
                    Assert.InRange(value, 0, seen.Length - 1);
                    Interlocked.Increment(ref seen[value]);
                    spin.Reset();
                }
                else spin.SpinOnce();
            }
        })).ToArray();
        var producers = Enumerable.Range(0, workers).Select(worker => Task.Run(() =>
        {
            for (int index = 0; index < perWorker; index++)
            {
                var spin = new SpinWait();
                while (!buffer.TryEnqueue(worker * perWorker + index))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    spin.SpinOnce();
                }
            }
            Interlocked.Increment(ref finished);
        })).ToArray();
        await Task.WhenAll(producers.Concat(consumers)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.All(seen, count => Assert.Equal(1, count));
        Assert.Empty(buffer);
    }
}

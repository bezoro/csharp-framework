using BenchmarkDotNet.Attributes;
using Bezoro.Events.Services;

namespace Bezoro.Events.Benchmarks;

[MemoryDiagnoser]
public class EventBusBenchmarks
{
	private EventBus _bus = null!;

	[Params(1, 128)]
	public int BatchSize { get; set; }

	[GlobalSetup]
	public void Setup() => _bus = new EventBus();

	[GlobalCleanup]
	public void Cleanup() => _bus.Dispose();

	[Benchmark]
	public int EnqueueAndFlush()
	{
		for (int i = 0; i < BatchSize; i++) _bus.Enqueue(new Message(i));
		return _bus.FlushQueued(BatchSize);
	}

	private readonly record struct Message(int Value);
}

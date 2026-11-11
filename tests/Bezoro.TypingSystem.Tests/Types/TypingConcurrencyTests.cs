using System.Collections.Concurrent;
using Bezoro.TypingSystem.Types;

namespace Bezoro.TypingSystem.Tests.Types;

public sealed class TypingConcurrencyTests
{
	[Fact]
	public void ConcurrentRecorders_PreserveEveryInputAndPublishCoherentSnapshots()
	{
		var metrics = new TypingMetrics();
		Parallel.For(0, 8, worker =>
		{
			for (int i = 0; i < 5000; i++)
			{
				metrics.Record((worker % 3) switch
				{
					0 => TypingResult.Match('a', 0, 'a', 2),
					1 => TypingResult.Mismatch('a', 0, 'b', 2),
					_ => TypingResult.EmptyTarget(0, 'a')
				});
				AssertCoherent(metrics.CreateSnapshot());
			}
		});
		var snapshot = metrics.CreateSnapshot();
		Assert.Equal(40_000u, snapshot.TotalInputs);
		Assert.Equal(15_000u, snapshot.CorrectInputs);
		Assert.Equal(15_000u, snapshot.MistakeInputs);
		Assert.Equal(10_000u, snapshot.FaultedInputs);
		Assert.Equal(0.5d, snapshot.Accuracy);
	}

	[Fact]
	public void ResetAndRecord_CannotExposeHalfResetCounters()
	{
		var metrics = new TypingMetrics();
		Parallel.For(0, 4, worker =>
		{
			for (int i = 0; i < 3000; i++)
			{
				if (worker == 0) metrics.Reset();
				else metrics.Record(TypingResult.Match('a', 0, 'a', 2));
				AssertCoherent(metrics.CreateSnapshot());
				Assert.InRange(metrics.Accuracy, 0d, 1d);
			}
		});
	}

	[Fact]
	public void ConcurrentAppendAndConsumption_PreserveAllWordsAcrossCapacityGrowth()
	{
		const int count = 20_000;
		var provider = new ArrayWordProvider();
		var consumed = new ConcurrentBag<string>();
		Parallel.Invoke(
			() => Parallel.For(0, count, i => provider.AddWord(i.ToString().AsMemory())),
			() =>
			{
				for (int i = 0; i < count; i++)
					if (provider.TryGetNextWord(out var word)) consumed.Add(word.ToString());
			});
		while (provider.TryGetNextWord(out var word)) consumed.Add(word.ToString());
		Assert.Equal(count, provider.WordCount);
		Assert.Equal(count, consumed.Count);
		Assert.Equal(count, consumed.Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public async Task BatchEnumeration_DoesNotHoldTheProviderGateWhileCallingUserCode()
	{
		var provider = new ArrayWordProvider(["existing"]);
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		IEnumerable<ReadOnlyMemory<char>> Words()
		{
			entered.Set();
			if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
			yield return "appended".AsMemory();
		}
		var append = Task.Run(() => provider.AddWords(Words()));
		try
		{
			Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
			var consume = Task.Run(() => provider.TryGetNextWord(out var word) ? word.ToString() : null);
			Assert.Equal("existing", await consume.WaitAsync(TimeSpan.FromSeconds(10)));
		}
		finally { release.Set(); await append.WaitAsync(TimeSpan.FromSeconds(10)); }
		Assert.True(provider.TryGetNextWord(out var next));
		Assert.Equal("appended", next.ToString());
	}

	private static void AssertCoherent(TypingMetricsSnapshot snapshot)
	{
		Assert.Equal(snapshot.TotalInputs, snapshot.CorrectInputs + snapshot.MistakeInputs + snapshot.FaultedInputs);
		Assert.Equal(snapshot.TotalEvaluated, snapshot.CorrectInputs + snapshot.MistakeInputs);
		Assert.InRange(snapshot.Accuracy, 0d, 1d);
		Assert.True(snapshot.Elapsed >= TimeSpan.Zero);
	}
}

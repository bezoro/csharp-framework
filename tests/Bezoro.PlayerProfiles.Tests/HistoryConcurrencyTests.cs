using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Bezoro.PlayerProfiles.Tests;

public sealed class HistoryConcurrencyTests
{
    private static PlayerMatchRecord Match(string id) => new(id, "alice", "bob", PlayerMatchOutcome.Win,
        DateTimeOffset.UnixEpoch, "training");

    [Fact]
    public void ConcurrentRetriesAppendOneRecordAndRetainItsIdentity()
    {
        var history = new PlayerMatchHistory("alice");
        int added = 0;
        Parallel.For(0, 1_000, _ =>
        {
            var result = history.Append(Match("same"));
            Assert.True(result is MatchHistoryAppendResult.Added or MatchHistoryAppendResult.AlreadyRecorded);
            if (result == MatchHistoryAppendResult.Added) Interlocked.Increment(ref added);
        });
        Assert.Equal(1, added);
        Assert.Equal(1, history.Count);
        Assert.Equal(Match("same"), history[0]);
    }

    [Fact]
    public void ConcurrentAppendLookupAndSnapshotsRetainAllRecords()
    {
        var history = new PlayerMatchHistory("alice", capacity: 1_000);
        Parallel.Invoke(() =>
        {
            Parallel.For(0, 1_000, index => Assert.Equal(MatchHistoryAppendResult.Added, history.Append(Match("match-" + index))));
        }, () =>
        {
            for (int index = 0; index < 500; index++)
            {
                var records = history.CreateSnapshot();
                Assert.Equal(records.Length, records.Select(record => record.MatchId).Distinct().Count());
                foreach (var record in records)
                {
                    Assert.True(history.TryGet(record.MatchId, out var found));
                    Assert.Equal(record, found);
                }
            }
        });
        Assert.Equal(1_000, history.Count);
        var snapshot = history.CreateSnapshot();
        snapshot[0] = default;
        Assert.True(history[0].IsValid);
    }
}

using Bezoro.Simulation.Attributes;
using Xunit;

namespace Bezoro.Simulation.Tests.Attributes;

public sealed class StatSetConcurrencyTests
{
    private static readonly StatId Damage = new(1);

    [Fact]
    public void ConcurrentModifierUpdatesRetainEveryHandleAndInvalidateCachedValues()
    {
        const int workers = 4;
        const int perWorker = 250;
        var set = new StatSet(new StatDefinition[] { new(Damage, 10) }, workers * perWorker);
        var handles = new StatModifierHandle[workers * perWorker];
        using var start = new Barrier(workers);
        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
        {
            start.SignalAndWait();
            for (int i = 0; i < perWorker; i++)
                handles[worker * perWorker + i] = set.AddModifier(Damage, new(StatModifierOperation.Add, 1));
        });

        Assert.Equal(handles.Length, handles.Distinct().Count());
        Assert.Equal(handles.Length, set.ModifierCount);
        Assert.Equal(handles.Length + 10, set.GetValue(Damage));
        Parallel.For(0, handles.Length, i =>
        {
            Assert.True(set.TryUpdateModifier(handles[i], new(StatModifierOperation.Add, 2)));
            Assert.InRange(set.GetValue(Damage), handles.Length + 10, handles.Length * 2 + 10);
        });
        Assert.Equal(handles.Length * 2 + 10, set.GetValue(Damage));
        Parallel.For(0, handles.Length, i => Assert.True(set.TryRemoveModifier(handles[i])));
        Assert.Equal(0, set.ModifierCount);
        Assert.Equal(handles.Length, set.AvailableModifierSlots);
        Assert.Equal(10, set.GetValue(Damage));
    }

    [Fact]
    public void ConcurrentRemovalAcceptsOneOwnerAndReturnsOneSlot()
    {
        var set = new StatSet(new StatDefinition[] { new(Damage, 10) }, 1);
        var handle = set.AddModifier(Damage, new(StatModifierOperation.Add, 5));
        int removed = 0;
        Parallel.For(0, 64, _ =>
        {
            if (set.TryRemoveModifier(handle)) Interlocked.Increment(ref removed);
        });
        Assert.Equal(1, removed);
        Assert.Equal(1, set.AvailableModifierSlots);
        Assert.Equal(0, set.ModifierCount);
        Assert.Equal(10, set.Evaluate(Damage).Value);
    }
}

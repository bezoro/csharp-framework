using System;
using Bezoro.Core.Types;
using Xunit;

namespace Bezoro.Core.Tests.Types;

public sealed class SwapbackArrayPredicateOwnershipTests
{
    [Fact]
    public void PredicateCannotAppendAValueThatCompactionWouldSilentlyDiscard()
    {
        var values = new SwapbackArray<int> { 1, 2, 3 };
        Assert.Throws<InvalidOperationException>(() => values.RemoveAll(value =>
        {
            if (value == 1) values.Add(4);
            return value == 2;
        }));
        Assert.Equal(new[] { 1, 2, 3, 4 }, values.ToArray());
    }

    [Fact]
    public void PredicateCannotClearStorageBeforeCompactionReadsItAgain()
    {
        var values = new SwapbackArray<int> { 1, 2, 3 };
        Assert.Throws<InvalidOperationException>(() => values.RemoveAll(_ =>
        {
            values.Clear();
            return false;
        }));
        Assert.Empty(values);
    }
}

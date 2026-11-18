using System;
using Bezoro.Core.Types;
using Xunit;

namespace Bezoro.Core.Tests.Types;

public sealed class SwapbackArrayPredicateOwnershipTests
{
    [Fact]
    public void ThrowAfterMatchingAndKeepingElementsPreservesContentsAndVersion()
    {
        var values = new SwapbackArray<int> { 1, 2, 3 };
        var version = values.Version;
        Assert.Throws<FormatException>(() => values.RemoveAll(value =>
            value == 3 ? throw new FormatException() : value == 1));
        Assert.Equal(new[] { 1, 2, 3 }, values.ToArray());
        Assert.Equal(version, values.Version);
    }

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

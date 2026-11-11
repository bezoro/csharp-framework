using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Bezoro.Commerce.Tests;

public sealed class CommerceConcurrencyTests
{
    private static StoreCatalog Catalog() => new(new[] { new SkinDefinition("skin", "sprite", null) },
        new[] { new StoreOffer("offer", "skin", "coins", 1) });

    [Fact]
    public void ConcurrentDistinctCreditsCannotLoseBalanceOrLedgerEntries()
    {
        var account = new CommerceAccount(Catalog(), "alice", receiptCapacity: 2_000);
        Parallel.For(0, 2_000, index =>
            Assert.Equal(CommerceTransactionStatus.Applied, account.CreditCurrency("credit-" + index, "coins", 1).Status));
        Assert.Equal(2_000, account.GetBalance("coins"));
        Assert.Equal(2_000, account.ReceiptCount);
    }

    [Fact]
    public void ConcurrentIdenticalTransactionsCreditAndPurchaseExactlyOnce()
    {
        var account = new CommerceAccount(Catalog(), "alice");
        int credits = 0, purchases = 0;
        Parallel.For(0, 1_000, _ =>
        {
            var status = account.CreditCurrency("credit", "coins", 10).Status;
            Assert.True(status is CommerceTransactionStatus.Applied or CommerceTransactionStatus.AlreadyApplied);
            if (status == CommerceTransactionStatus.Applied) Interlocked.Increment(ref credits);
        });
        Parallel.For(0, 1_000, _ =>
        {
            var status = account.Purchase("purchase", "offer", DateTimeOffset.UtcNow).Status;
            Assert.True(status is CommerceTransactionStatus.Applied or CommerceTransactionStatus.AlreadyApplied);
            if (status == CommerceTransactionStatus.Applied) Interlocked.Increment(ref purchases);
        });
        Assert.Equal(1, credits);
        Assert.Equal(1, purchases);
        Assert.Equal(9, account.GetBalance("coins"));
        Assert.Equal(2, account.ReceiptCount);
        Assert.Equal(1, account.OwnedSkinCount);
    }

    [Fact]
    public void ConcurrentCreditsRespectLedgerCapacityAndSnapshotsAreCoherent()
    {
        var limited = new CommerceAccount(Catalog(), "alice", receiptCapacity: 1);
        Parallel.For(0, 1_000, index => limited.CreditCurrency("credit-" + index, "coins", 1));
        Assert.Equal(1, limited.ReceiptCount);
        Assert.Equal(1, limited.GetBalance("coins"));

        var account = new CommerceAccount(Catalog(), "alice", receiptCapacity: 1_000);
        Parallel.Invoke(() =>
        {
            for (int index = 0; index < 1_000; index++) account.CreditCurrency("credit-" + index, "coins", 1);
        }, () =>
        {
            for (int index = 0; index < 1_000; index++)
            {
                var snapshot = account.CreateSnapshot();
                Assert.Equal(snapshot.Receipts.Length, snapshot.Balances.IsEmpty ? 0 : snapshot.Balances[0].Amount);
            }
        });
    }
}

using Bezoro.Commerce;
using Xunit;

namespace Bezoro.Commerce.Tests;

public class CommerceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static StoreCatalog Catalog() => new(
        new[] { new SkinDefinition("marble", "sprites/marble", "prefabs/marble"), new SkinDefinition("gold", "sprites/gold", null) },
        new[] { new StoreOffer("marble-offer", "marble", "coins", 100, Now, Now.AddDays(1)), new StoreOffer("gold-free", "gold", "coins", 0) });

    [Fact]
    public void Catalog_copies_inputs_and_exposes_both_asset_modes()
    {
        var skins = new[] { new SkinDefinition("marble", "sprites/marble", "prefabs/marble") };
        var catalog = new StoreCatalog(skins, Array.Empty<StoreOffer>());
        skins[0] = default;
        Assert.True(catalog.TryGetSkin("marble", out var skin));
        Assert.Equal("sprites/marble", skin.Asset2D);
        Assert.Equal("prefabs/marble", skin.Asset3D);
    }

    [Fact]
    public void Invalid_catalogs_and_empty_asset_sets_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new SkinDefinition("none", null, null));
        Assert.Throws<ArgumentException>(() => new StoreCatalog(new SkinDefinition[1], Array.Empty<StoreOffer>()));
        Assert.Throws<ArgumentException>(() => new StoreCatalog(Array.Empty<SkinDefinition>(), new[] { new StoreOffer("a", "missing", "coins", 1) }));
        var skin = new SkinDefinition("a", "a2d", null);
        Assert.Throws<ArgumentException>(() => new StoreCatalog(new[] { skin, skin }, Array.Empty<StoreOffer>()));
        Assert.Throws<ArgumentException>(() => new StoreOffer("a", "a", "coins", 1, Now, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StoreOffer("a", "a", "coins", -1));
    }

    [Fact]
    public void Purchase_deducts_once_and_equipment_is_shared_between_renderers()
    {
        var account = new CommerceAccount(Catalog(), "alice");
        account.CreditCurrency("reward-1", "coins", 150);
        var purchase = account.Purchase("purchase-1", "marble-offer", Now);
        Assert.Equal(CommerceTransactionStatus.Applied, purchase.Status);
        Assert.Equal(50, account.GetBalance("coins"));
        Assert.True(account.OwnsSkin("marble"));
        Assert.True(account.TryEquipSkin("marble"));
        Assert.Equal("marble", account.EquippedSkinId);
        var retry = account.Purchase("purchase-1", "marble-offer", Now.AddDays(2));
        Assert.Equal(CommerceTransactionStatus.AlreadyApplied, retry.Status);
        Assert.Equal(purchase.Receipt!.Value.TransactionId, retry.Receipt!.Value.TransactionId);
        Assert.Equal(50, retry.Receipt.Value.BalanceAfter);
        Assert.Equal(50, account.GetBalance("coins"));
        Assert.Equal(2, account.ReceiptCount);
    }

    [Fact]
    public void Failed_purchase_does_not_spend_or_reserve_transaction_key()
    {
        var account = new CommerceAccount(Catalog(), "alice");
        Assert.Equal(CommerceTransactionStatus.InsufficientFunds, account.Purchase("buy", "marble-offer", Now).Status);
        Assert.Equal(0, account.ReceiptCount);
        Assert.False(account.OwnsSkin("marble"));
        account.CreditCurrency("reward", "coins", 100);
        Assert.True(account.Purchase("buy", "marble-offer", Now).Succeeded);
        Assert.Equal(0, account.GetBalance("coins"));
    }

    [Fact]
    public void Conflicting_transactions_cannot_change_kind_target_or_credit_amount()
    {
        var account = new CommerceAccount(Catalog(), "alice");
        Assert.True(account.CreditCurrency("credit", "coins", 100).Succeeded);
        Assert.Equal(CommerceTransactionStatus.AlreadyApplied, account.CreditCurrency("credit", "coins", 100).Status);
        Assert.Equal(CommerceTransactionStatus.ConflictingTransaction, account.CreditCurrency("credit", "coins", 101).Status);
        Assert.Equal(CommerceTransactionStatus.ConflictingTransaction, account.CreditCurrency("credit", "gems", 100).Status);
        Assert.Equal(CommerceTransactionStatus.ConflictingTransaction, account.Purchase("credit", "marble-offer", Now).Status);
        Assert.Equal(100, account.GetBalance("coins"));
        Assert.Equal(0, account.GetBalance("gems"));
    }

    [Fact]
    public void Offer_window_is_start_inclusive_and_end_exclusive()
    {
        var account = new CommerceAccount(Catalog(), "alice");
        account.CreditCurrency("credit", "coins", 100);
        Assert.Equal(CommerceTransactionStatus.OfferUnavailable, account.Purchase("buy", "marble-offer", Now.AddTicks(-1)).Status);
        Assert.Equal(CommerceTransactionStatus.OfferUnavailable, account.Purchase("buy", "marble-offer", Now.AddDays(1)).Status);
        Assert.Equal(100, account.GetBalance("coins"));
        Assert.True(account.Purchase("buy", "marble-offer", Now).Succeeded);
    }

    [Fact]
    public void Buying_owned_skin_does_not_spend_again_and_free_offers_work_without_wallet()
    {
        var account = new CommerceAccount(Catalog(), "alice");
        Assert.True(account.Purchase("free", "gold-free", Now).Succeeded);
        Assert.Equal(CommerceTransactionStatus.AlreadyOwned, account.Purchase("another", "gold-free", Now).Status);
        Assert.Equal(0, account.GetBalance("coins"));
        Assert.Equal(1, account.ReceiptCount);
        Assert.False(account.TryEquipSkin("marble"));
        Assert.Null(account.EquippedSkinId);
    }

    [Fact]
    public void Grants_are_idempotent_and_unknown_products_do_not_change_state()
    {
        var account = new CommerceAccount(Catalog(), "alice");
        Assert.Equal(CommerceTransactionStatus.UnknownProduct, account.GrantSkin("missing", "unknown").Status);
        Assert.Equal(CommerceTransactionStatus.UnknownProduct, account.Purchase("missing", "unknown", Now).Status);
        Assert.True(account.GrantSkin("story-reward", "marble").Succeeded);
        Assert.Equal(CommerceTransactionStatus.AlreadyApplied, account.GrantSkin("story-reward", "marble").Status);
        Assert.Equal(CommerceTransactionStatus.AlreadyOwned, account.GrantSkin("second", "marble").Status);
        Assert.Equal(1, account.OwnedSkinCount);
    }

    [Fact]
    public void Ledger_full_rejects_new_operations_but_keeps_retry_protection()
    {
        var account = new CommerceAccount(Catalog(), "alice", receiptCapacity: 1);
        account.CreditCurrency("credit", "coins", 100);
        Assert.Equal(CommerceTransactionStatus.ReceiptCapacityReached, account.Purchase("buy", "marble-offer", Now).Status);
        Assert.Equal(CommerceTransactionStatus.ReceiptCapacityReached, account.GrantSkin("grant", "gold").Status);
        Assert.Equal(CommerceTransactionStatus.ReceiptCapacityReached, account.CreditCurrency("second", "coins", 1).Status);
        Assert.Equal(CommerceTransactionStatus.AlreadyApplied, account.CreditCurrency("credit", "coins", 100).Status);
        Assert.Equal(100, account.GetBalance("coins"));
        Assert.False(account.OwnsSkin("marble"));
    }

    [Fact]
    public void Overflow_credit_is_rejected_without_partial_mutation()
    {
        var account = new CommerceAccount(Catalog(), "alice");
        account.CreditCurrency("max", "coins", long.MaxValue);
        Assert.Equal(CommerceTransactionStatus.BalanceOverflow, account.CreditCurrency("extra", "coins", 1).Status);
        Assert.Equal(long.MaxValue, account.GetBalance("coins"));
        Assert.Equal(1, account.ReceiptCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => account.CreditCurrency("negative", "coins", -1));
        Assert.False(default(CommerceTransactionResult).Succeeded);
    }

    [Fact]
    public void Snapshot_roundtrip_preserves_balances_ownership_equipment_and_retry_receipts()
    {
        var catalog = Catalog();
        var original = new CommerceAccount(catalog, "alice");
        original.CreditCurrency("credit", "coins", 150);
        original.Purchase("buy", "marble-offer", Now);
        original.TryEquipSkin("marble");
        var snapshot = original.CreateSnapshot();
        var restored = new CommerceAccount(catalog, snapshot);
        Assert.Equal("alice", restored.AccountId);
        Assert.Equal(50, restored.GetBalance("coins"));
        Assert.True(restored.OwnsSkin("marble"));
        Assert.Equal("marble", restored.EquippedSkinId);
        Assert.Equal(CommerceTransactionStatus.AlreadyApplied, restored.Purchase("buy", "marble-offer", Now.AddYears(1)).Status);
        original.CreditCurrency("later", "coins", 7);
        Assert.Equal(50, snapshot.Balances[0].Amount);
        Assert.Equal(50, restored.GetBalance("coins"));
    }

    [Fact]
    public void Public_snapshot_copies_input_buffers()
    {
        var balances = new[] { new WalletBalance("coins", 10) };
        var skins = new[] { "marble" };
        var snapshot = new CommerceAccountSnapshot("alice", balances, skins, Array.Empty<CommerceReceipt>());
        balances[0] = new WalletBalance("coins", 999);
        skins[0] = "unknown";
        var restored = new CommerceAccount(Catalog(), snapshot);
        Assert.Equal(10, restored.GetBalance("coins"));
        Assert.True(restored.OwnsSkin("marble"));
    }

    [Fact]
    public void Restore_rejects_malformed_and_duplicate_entries_without_exposing_an_account()
    {
        var catalog = Catalog();
        Assert.Throws<ArgumentException>(() => new CommerceAccount(catalog,
            new CommerceAccountSnapshot("alice", new WalletBalance[1], Array.Empty<string>(), Array.Empty<CommerceReceipt>())));
        Assert.Throws<ArgumentException>(() => new CommerceAccount(catalog,
            new CommerceAccountSnapshot("alice", Array.Empty<WalletBalance>(), new[] { "unknown" }, Array.Empty<CommerceReceipt>())));
        Assert.Throws<ArgumentException>(() => new CommerceAccount(catalog,
            new CommerceAccountSnapshot("alice", Array.Empty<WalletBalance>(), Array.Empty<string>(), Array.Empty<CommerceReceipt>(), "marble")));
        var receipt = new CommerceReceipt("a", CommerceOperation.GrantSkin, "marble", 0, 0);
        Assert.Throws<ArgumentException>(() => new CommerceAccount(catalog,
            new CommerceAccountSnapshot("alice", Array.Empty<WalletBalance>(), Array.Empty<string>(), new[] { receipt, receipt })));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommerceAccount(catalog,
            new CommerceAccountSnapshot("alice", Array.Empty<WalletBalance>(), Array.Empty<string>(), new[] { receipt }), receiptCapacity: 0));
    }

    [Fact]
    public void Read_queries_and_successful_retries_allocate_no_managed_storage()
    {
        var account = new CommerceAccount(Catalog(), "alice");
        account.CreditCurrency("credit", "coins", 100);
        account.GrantSkin("grant", "marble");
        for (int index = 0; index < 100; index++) account.CreditCurrency("credit", "coins", 100);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 100; index++)
        {
            account.GetBalance("coins");
            account.OwnsSkin("marble");
            account.TryEquipSkin("marble");
            account.CreditCurrency("credit", "coins", 100);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

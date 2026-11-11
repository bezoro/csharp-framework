# Bezoro.Commerce

Optional cosmetic ownership and an in-game currency store. The module has no Unity dependency, loaded assets, network workers or frame updates. It supports .NET 10 and .NET Standard 2.1. It does not process real money.

```csharp
var catalog = new StoreCatalog(
    new[] { new SkinDefinition("marble", "sprites/marble-set", "prefabs/marble-set") },
    new[] { new StoreOffer("marble-100", "marble", "coins", price: 100) });

var account = new CommerceAccount(catalog, "alice", receiptCapacity: 4096);
account.CreditCurrency("quest-42-reward", "coins", 150); // Authoritative host only.
var purchase = account.Purchase("purchase-42", "marble-100", serverTime);
if (purchase.Succeeded)
{
    account.TryEquipSkin("marble");
    CommerceAccountSnapshot snapshot = account.CreateSnapshot();
    // Persist the full snapshot atomically before acknowledging the purchase.
}
```

One cosmetic identity can refer to both a 2D sprite set and a 3D prefab set. The renderer resolves these opaque asset IDs; switching board presentation reads the same equipped identity. Asset loading, fallbacks for a presentation absent from a skin, animation and GPU resources remain renderer responsibilities. A skin definition must support at least one presentation.

Catalogs copy their inputs and can be shared across accounts and threads. Offer start times are inclusive and end times exclusive. `Purchase` receives authoritative time explicitly. Prices and balances use nonnegative integral `long` values, so currency arithmetic is exact. Free offers are supported without creating a wallet entry. Identifiers use ordinal, case-sensitive comparison.

The account owns mutable wallet, permanent ownership, equipment and successful transaction receipts. Public operations serialize through one private gate per account. Concurrent retries apply a transaction once, and snapshots copy balances, ownership, equipment and receipts from one coherent state. Independent accounts can execute on different workers. A sequence of separate method calls is not a transaction. Purchases validate complete payment and ownership before changing state. Expected failures return `CommerceTransactionStatus`; invalid arguments throw. Read queries, equipment changes and identical retries do not allocate managed storage after construction. New entries grow dictionaries on demand; explicit snapshots allocate copied arrays. This module creates no tasks or per-frame work.

Every successful credit, grant or purchase retains an account-scoped transaction ID. Reusing the same ID and request returns its original receipt, including after an offer expires or after snapshot restoration. Reusing it for another operation, target or credit amount returns `ConflictingTransaction`. A failed request reserves no ID and can be retried after its cause is resolved. Buying or granting an already-owned skin never charges again; applications choose whether duplicate progression rewards should become alternate currency.

The receipt ledger has a finite, explicit capacity and never silently evicts old IDs. Once full, new mutations fail with `ReceiptCapacityReached`; existing retries still work. This keeps memory bounded and avoids replaying old purchases after eviction. Retain the complete ledger in durable storage; restore with a larger explicit capacity when appropriate. Large services should place the authoritative transaction ledger in their database and enforce persistent uniqueness there. This in-memory account is not a distributed database.

`CreateSnapshot` includes wallet, owned skins, equipment and receipts. `CommerceAccount(catalog, snapshot, receiptCapacity)` restores all of them and checks default/duplicate entries, known ownership, equipped ownership, and permanent ownership for every successful direct skin-grant receipt. Receipt construction also rejects impossible pre-operation balances: a credit cannot leave less than its amount, and a purchase's cost plus remaining balance must fit `long`. Keep retired skin definitions while accounts retain their entitlements; historical receipt targets need not still have an active offer. Public snapshot construction copies caller buffers, allowing a host serializer to rehydrate the values without reflection-based mutation. Array order in a snapshot is not a persistence contract.

Never accept a client-provided balance, grant, purchase outcome, clock or snapshot as authoritative. Authentication, reward authorization, persistence, catalog rollout, cross-account concurrency, payment-provider validation and real-money refunds belong to the host. Persist state and its receipt in one transaction before replying to a network request. For quest or battle-pass rewards, use a stable claim identity as the commerce transaction ID and coordinate reward delivery with the host's durable progression transaction. Separate modules deliberately do not pretend to provide a distributed transaction.


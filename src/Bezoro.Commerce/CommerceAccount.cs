namespace Bezoro.Commerce;

/// <summary>Owns balances, permanent cosmetic entitlements, equipment and a bounded transaction ledger.</summary>
/// <remarks>
/// Requires one owner or external serialization. Successful business operations are atomic in memory; rejected operations do not mutate state.
/// Persist the complete snapshot and transaction receipt together before acknowledging network requests. This class does not authenticate callers,
/// authorize rewards, process real money or provide distributed transactions. Only the authoritative host may grant currency or ownership.
/// </remarks>
public sealed class CommerceAccount
{
    private readonly Dictionary<string, long> _balances = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ownedSkins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CommerceReceipt> _receipts = new(StringComparer.Ordinal);

    /// <summary>Creates an empty account with a finite successful-transaction limit. Storage grows with actual operations.</summary>
    public CommerceAccount(StoreCatalog catalog, string accountId, int receiptCapacity = 1024)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Identifiers.Require(accountId, nameof(accountId));
        if (receiptCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(receiptCapacity));
        AccountId = accountId;
        ReceiptCapacity = receiptCapacity;
    }

    /// <summary>Restores trusted persisted state, rejecting duplicate entries, unknown skins, invalid receipts and unowned equipment.</summary>
    /// <remarks>Keep retired skin definitions in the catalog while accounts retain their ownership.</remarks>
    public CommerceAccount(StoreCatalog catalog, CommerceAccountSnapshot snapshot, int receiptCapacity = 1024)
        : this(catalog, (snapshot ?? throw new ArgumentNullException(nameof(snapshot))).AccountId, receiptCapacity)
    {
        if (snapshot.Receipts.Length > receiptCapacity) throw new ArgumentException("The snapshot exceeds receipt capacity.", nameof(snapshot));
        foreach (var balance in snapshot.Balances)
        {
            if (string.IsNullOrWhiteSpace(balance.CurrencyId) || _balances.ContainsKey(balance.CurrencyId))
                throw new ArgumentException("Snapshot currencies must be initialized and unique.", nameof(snapshot));
            _balances.Add(balance.CurrencyId, balance.Amount);
        }
        foreach (string skin in snapshot.OwnedSkins)
        {
            if (string.IsNullOrWhiteSpace(skin) || !Catalog.TryGetSkin(skin, out _) || !_ownedSkins.Add(skin))
                throw new ArgumentException("Owned skins must be known and unique.", nameof(snapshot));
        }
        foreach (var receipt in snapshot.Receipts)
        {
            if (!receipt.IsValid || _receipts.ContainsKey(receipt.TransactionId))
                throw new ArgumentException("Snapshot receipts must be initialized and unique.", nameof(snapshot));
            if (receipt.Operation == CommerceOperation.GrantSkin && !_ownedSkins.Contains(receipt.TargetId))
                throw new ArgumentException("A successful skin-grant receipt requires the corresponding permanent ownership.", nameof(snapshot));
            _receipts.Add(receipt.TransactionId, receipt);
        }
        if (snapshot.EquippedSkinId is not null && !TryEquipSkin(snapshot.EquippedSkinId))
            throw new ArgumentException("The equipped skin must be owned.", nameof(snapshot));
    }

    /// <summary>Gets the immutable catalog shared with other accounts.</summary>
    public StoreCatalog Catalog { get; }
    /// <summary>Gets the account identity.</summary>
    public string AccountId { get; }
    /// <summary>Gets the maximum successful-transaction count; receipts are never silently evicted.</summary>
    public int ReceiptCapacity { get; }
    /// <summary>Gets the retained successful-transaction count.</summary>
    public int ReceiptCount => _receipts.Count;
    /// <summary>Gets the number of owned skins.</summary>
    public int OwnedSkinCount => _ownedSkins.Count;
    /// <summary>Gets the optional equipped skin identifier, independent of the active 2D or 3D renderer.</summary>
    public string? EquippedSkinId { get; private set; }

    /// <summary>Gets a currency balance, or zero when that currency has never been credited.</summary>
    public long GetBalance(string currencyId)
    {
        Identifiers.Require(currencyId, nameof(currencyId));
        return _balances.TryGetValue(currencyId, out long amount) ? amount : 0;
    }

    /// <summary>Checks permanent ownership without allocating.</summary>
    public bool OwnsSkin(string skinId)
    {
        Identifiers.Require(skinId, nameof(skinId));
        return _ownedSkins.Contains(skinId);
    }

    /// <summary>Credits positive currency units once for an account-scoped transaction identifier.</summary>
    public CommerceTransactionResult CreditCurrency(string transactionId, string currencyId, long amount)
    {
        Identifiers.Require(transactionId, nameof(transactionId));
        Identifiers.Require(currencyId, nameof(currencyId));
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (TryReplay(transactionId, CommerceOperation.CreditCurrency, currencyId, amount, out var replay)) return replay;
        if (_receipts.Count == ReceiptCapacity) return Failure(CommerceTransactionStatus.ReceiptCapacityReached);
        long current = GetBalance(currencyId);
        if (amount > long.MaxValue - current) return Failure(CommerceTransactionStatus.BalanceOverflow);
        var receipt = new CommerceReceipt(transactionId, CommerceOperation.CreditCurrency, currencyId, amount, current + amount);
        _balances[currencyId] = receipt.BalanceAfter;
        _receipts.Add(transactionId, receipt);
        return Applied(receipt);
    }

    /// <summary>Grants a catalog skin once for an account-scoped transaction identifier; ownership is permanent.</summary>
    public CommerceTransactionResult GrantSkin(string transactionId, string skinId)
    {
        Identifiers.Require(transactionId, nameof(transactionId));
        Identifiers.Require(skinId, nameof(skinId));
        if (TryReplay(transactionId, CommerceOperation.GrantSkin, skinId, 0, out var replay)) return replay;
        if (!Catalog.TryGetSkin(skinId, out _)) return Failure(CommerceTransactionStatus.UnknownProduct);
        if (_ownedSkins.Contains(skinId)) return Failure(CommerceTransactionStatus.AlreadyOwned);
        if (_receipts.Count == ReceiptCapacity) return Failure(CommerceTransactionStatus.ReceiptCapacityReached);
        var receipt = new CommerceReceipt(transactionId, CommerceOperation.GrantSkin, skinId, 0, 0);
        _ownedSkins.Add(skinId);
        _receipts.Add(transactionId, receipt);
        return Applied(receipt);
    }

    /// <summary>Purchases one permanent skin with a full, atomic deduction at authoritative time.</summary>
    /// <remarks>Identical successful retries return their original receipt even after the offer expires. Rejected requests reserve no transaction key.</remarks>
    public CommerceTransactionResult Purchase(string transactionId, string offerId, DateTimeOffset now)
    {
        Identifiers.Require(transactionId, nameof(transactionId));
        Identifiers.Require(offerId, nameof(offerId));
        if (TryReplay(transactionId, CommerceOperation.Purchase, offerId, 0, out var replay)) return replay;
        if (!Catalog.TryGetOffer(offerId, out var offer)) return Failure(CommerceTransactionStatus.UnknownProduct);
        if (!offer.IsAvailableAt(now)) return Failure(CommerceTransactionStatus.OfferUnavailable);
        if (_ownedSkins.Contains(offer.SkinId)) return Failure(CommerceTransactionStatus.AlreadyOwned);
        if (_receipts.Count == ReceiptCapacity) return Failure(CommerceTransactionStatus.ReceiptCapacityReached);
        long balance = GetBalance(offer.CurrencyId);
        if (balance < offer.Price) return Failure(CommerceTransactionStatus.InsufficientFunds);
        var receipt = new CommerceReceipt(transactionId, CommerceOperation.Purchase, offerId, offer.Price, balance - offer.Price);
        if (offer.Price != 0) _balances[offer.CurrencyId] = receipt.BalanceAfter;
        _ownedSkins.Add(offer.SkinId);
        _receipts.Add(transactionId, receipt);
        return Applied(receipt);
    }

    /// <summary>Equips an owned skin without changing ownership or loading assets; false leaves equipment unchanged.</summary>
    public bool TryEquipSkin(string skinId)
    {
        Identifiers.Require(skinId, nameof(skinId));
        if (!_ownedSkins.Contains(skinId)) return false;
        EquippedSkinId = skinId;
        return true;
    }

    /// <summary>Looks up a successful transaction receipt.</summary>
    public bool TryGetReceipt(string transactionId, out CommerceReceipt receipt)
    {
        Identifiers.Require(transactionId, nameof(transactionId));
        return _receipts.TryGetValue(transactionId, out receipt);
    }

    /// <summary>Copies the complete state for durable persistence. This explicit operation allocates.</summary>
    public CommerceAccountSnapshot CreateSnapshot()
    {
        var balances = new WalletBalance[_balances.Count];
        int index = 0;
        foreach (var pair in _balances) balances[index++] = new WalletBalance(pair.Key, pair.Value);
        var owned = new string[_ownedSkins.Count];
        _ownedSkins.CopyTo(owned);
        var receipts = new CommerceReceipt[_receipts.Count];
        _receipts.Values.CopyTo(receipts, 0);
        return new CommerceAccountSnapshot(AccountId, EquippedSkinId, balances, owned, receipts);
    }

    private bool TryReplay(string transactionId, CommerceOperation operation, string targetId, long amount, out CommerceTransactionResult result)
    {
        if (!_receipts.TryGetValue(transactionId, out var receipt)) { result = default; return false; }
        bool same = receipt.Operation == operation && receipt.TargetId == targetId &&
            (operation != CommerceOperation.CreditCurrency || receipt.Amount == amount);
        result = same ? new CommerceTransactionResult(CommerceTransactionStatus.AlreadyApplied, receipt) : Failure(CommerceTransactionStatus.ConflictingTransaction);
        return true;
    }

    private static CommerceTransactionResult Applied(CommerceReceipt receipt) => new(CommerceTransactionStatus.Applied, receipt);
    private static CommerceTransactionResult Failure(CommerceTransactionStatus status) => new(status);
}

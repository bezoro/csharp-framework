namespace Bezoro.Commerce;

/// <summary>Identifies a successful account operation.</summary>
public enum CommerceOperation
{
    /// <summary>Currency was granted by the authoritative host.</summary>
    CreditCurrency,
    /// <summary>A cosmetic was granted by the authoritative host.</summary>
    GrantSkin,
    /// <summary>An offer was purchased with in-game currency.</summary>
    Purchase
}

/// <summary>An immutable successful operation, retained to prevent duplicate spending or granting.</summary>
public readonly struct CommerceReceipt
{
    /// <summary>Rehydrates a receipt from trusted persistence. Target is the currency, skin or offer identifier for its operation.</summary>
    public CommerceReceipt(string transactionId, CommerceOperation operation, string targetId, long amount, long balanceAfter)
    {
        Identifiers.Require(transactionId, nameof(transactionId));
        Identifiers.Require(targetId, nameof(targetId));
        if (operation < CommerceOperation.CreditCurrency || operation > CommerceOperation.Purchase)
            throw new ArgumentOutOfRangeException(nameof(operation));
        if (amount < 0 || (operation == CommerceOperation.CreditCurrency && amount == 0) ||
            (operation == CommerceOperation.GrantSkin && amount != 0)) throw new ArgumentOutOfRangeException(nameof(amount));
        if (balanceAfter < 0 || (operation == CommerceOperation.GrantSkin && balanceAfter != 0))
            throw new ArgumentOutOfRangeException(nameof(balanceAfter));
        TransactionId = transactionId;
        Operation = operation;
        TargetId = targetId;
        Amount = amount;
        BalanceAfter = balanceAfter;
    }

    /// <summary>Gets the account-scoped idempotency key; null for the default value.</summary>
    public string TransactionId { get; }
    /// <summary>Gets the operation kind.</summary>
    public CommerceOperation Operation { get; }
    /// <summary>Gets the operation's currency, skin or offer identifier.</summary>
    public string TargetId { get; }
    /// <summary>Gets the credited amount or purchase cost; zero for a direct skin grant.</summary>
    public long Amount { get; }
    /// <summary>Gets the affected currency balance immediately after the operation; zero for a direct skin grant.</summary>
    public long BalanceAfter { get; }
    /// <summary>Gets whether this value was initialized through its constructor.</summary>
    public bool IsValid => TransactionId is not null;
}

/// <summary>Describes a commerce operation without using exceptions for expected business failures.</summary>
public enum CommerceTransactionStatus
{
    /// <summary>The result is the uninitialized default value.</summary>
    Uninitialized,
    /// <summary>The operation was applied.</summary>
    Applied,
    /// <summary>The same transaction was already applied; the original receipt is returned.</summary>
    AlreadyApplied,
    /// <summary>The transaction identifier was used for a different request.</summary>
    ConflictingTransaction,
    /// <summary>The account cannot afford the complete purchase.</summary>
    InsufficientFunds,
    /// <summary>The account already owns this skin.</summary>
    AlreadyOwned,
    /// <summary>The requested skin or offer does not exist.</summary>
    UnknownProduct,
    /// <summary>The offer is outside its time window.</summary>
    OfferUnavailable,
    /// <summary>The durable duplicate ledger has reached its configured limit.</summary>
    ReceiptCapacityReached,
    /// <summary>The currency credit would exceed a signed 64-bit balance.</summary>
    BalanceOverflow
}

/// <summary>The immutable result of a purchase or grant.</summary>
public readonly struct CommerceTransactionResult
{
    internal CommerceTransactionResult(CommerceTransactionStatus status, CommerceReceipt? receipt = null)
    {
        Status = status;
        Receipt = receipt;
    }
    /// <summary>Gets the operation outcome.</summary>
    public CommerceTransactionStatus Status { get; }
    /// <summary>Gets the successful operation's receipt, including on an identical retry.</summary>
    public CommerceReceipt? Receipt { get; }
    /// <summary>Gets whether the requested operation was applied previously or by this call.</summary>
    public bool Succeeded => Status == CommerceTransactionStatus.Applied || Status == CommerceTransactionStatus.AlreadyApplied;
}

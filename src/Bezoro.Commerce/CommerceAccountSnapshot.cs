namespace Bezoro.Commerce;

/// <summary>An immutable, nonnegative balance in one abstract in-game currency.</summary>
public readonly struct WalletBalance
{
    /// <summary>Creates a balance for trusted snapshot persistence.</summary>
    public WalletBalance(string currencyId, long amount)
    {
        Identifiers.Require(currencyId, nameof(currencyId));
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        CurrencyId = currencyId;
        Amount = amount;
    }
    /// <summary>Gets the currency identifier; null for the default value.</summary>
    public string CurrencyId { get; }
    /// <summary>Gets the nonnegative integral amount.</summary>
    public long Amount { get; }
}

/// <summary>A copied immutable account snapshot, including the durable transaction ledger.</summary>
/// <remarks>Creation copies inputs; the account constructor validates their contents against its catalog before restoration.</remarks>
public sealed class CommerceAccountSnapshot
{
    private readonly WalletBalance[] _balances;
    private readonly string[] _ownedSkins;
    private readonly CommerceReceipt[] _receipts;

    /// <summary>Copies trusted persistence data. The account identity must be nonempty.</summary>
    public CommerceAccountSnapshot(string accountId, ReadOnlySpan<WalletBalance> balances, ReadOnlySpan<string> ownedSkins,
        ReadOnlySpan<CommerceReceipt> receipts, string? equippedSkinId = null)
    {
        Identifiers.Require(accountId, nameof(accountId));
        AccountId = accountId;
        _balances = balances.ToArray();
        _ownedSkins = ownedSkins.ToArray();
        _receipts = receipts.ToArray();
        EquippedSkinId = equippedSkinId;
    }

    internal CommerceAccountSnapshot(string accountId, string? equippedSkinId, WalletBalance[] balances,
        string[] ownedSkins, CommerceReceipt[] receipts)
    {
        AccountId = accountId;
        _balances = balances;
        _ownedSkins = ownedSkins;
        _receipts = receipts;
        EquippedSkinId = equippedSkinId;
    }
    /// <summary>Gets the account identity.</summary>
    public string AccountId { get; }
    /// <summary>Gets the copied balances.</summary>
    public ReadOnlySpan<WalletBalance> Balances => _balances;
    /// <summary>Gets the copied owned skin identifiers.</summary>
    public ReadOnlySpan<string> OwnedSkins => _ownedSkins;
    /// <summary>Gets the copied successful transaction ledger.</summary>
    public ReadOnlySpan<CommerceReceipt> Receipts => _receipts;
    /// <summary>Gets the optional equipped skin identifier.</summary>
    public string? EquippedSkinId { get; }
}

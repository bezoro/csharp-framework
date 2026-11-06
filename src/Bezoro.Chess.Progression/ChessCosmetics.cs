using Bezoro.Commerce;

namespace Bezoro.Chess.Progression;

/// <summary>The independent presentation slot affected by a permanent cosmetic choice.</summary>
public enum ChessCosmeticSlot
{
    /// <summary>Board squares and their presentation.</summary>
    Board,
    /// <summary>The complete chess piece set.</summary>
    Pieces
}

/// <summary>Immutable local cosmetic content. The game resolves its ID to assets; this module loads no images or materials.</summary>
public sealed class ChessCosmeticDefinition
{
    /// <summary>Creates a board or piece offer. Each slot's default must be free; paid variants use local training points.</summary>
    public ChessCosmeticDefinition(string id, string title, ChessCosmeticSlot slot, long price, bool isDefault = false)
    {
        ProgressText.Require(id, nameof(id)); ProgressText.Require(title, nameof(title));
        if (slot is not (ChessCosmeticSlot.Board or ChessCosmeticSlot.Pieces)) throw new ArgumentOutOfRangeException(nameof(slot));
        if (price < 0 || isDefault && price != 0) throw new ArgumentOutOfRangeException(nameof(price));
        Id = id; Title = title; Slot = slot; Price = price; IsDefault = isDefault;
    }
    /// <summary>Gets the opaque cosmetic and host presentation-registry identity.</summary>
    public string Id { get; }
    /// <summary>Gets a default title, localizable by the host using the identity.</summary>
    public string Title { get; }
    /// <summary>Gets the board or piece slot.</summary>
    public ChessCosmeticSlot Slot { get; }
    /// <summary>Gets the nonnegative local training-point price.</summary>
    public long Price { get; }
    /// <summary>Gets whether every local profile owns and initially equips this free definition.</summary>
    public bool IsDefault { get; }
}

/// <summary>Shares typed cosmetics while delegating ownership, wallet and purchase rules to Bezoro.Commerce.</summary>
public sealed class ChessCosmeticCatalog
{
    internal const string Currency = "training-points";
    private readonly Dictionary<string, ChessCosmeticDefinition> _lookup = new(StringComparer.Ordinal);
    /// <summary>Copies definitions, requiring unique IDs and exactly one free default per slot.</summary>
    public ChessCosmeticCatalog(IEnumerable<ChessCosmeticDefinition> definitions)
    {
        if (definitions is null) throw new ArgumentNullException(nameof(definitions));
        var copied = definitions.ToArray();
        foreach (var definition in copied)
        {
            if (definition is null || _lookup.ContainsKey(definition.Id)) throw new ArgumentException("Cosmetic definitions must be nonnull and unique.", nameof(definitions));
            _lookup.Add(definition.Id, definition);
        }
        if (copied.Count(item => item.IsDefault && item.Slot == ChessCosmeticSlot.Board) != 1
            || copied.Count(item => item.IsDefault && item.Slot == ChessCosmeticSlot.Pieces) != 1)
            throw new ArgumentException("Exactly one free default board and piece set are required.", nameof(definitions));
        Definitions = Array.AsReadOnly(copied);
        Store = new StoreCatalog(copied.Select(item => new SkinDefinition(item.Id, item.Id, item.Id)).ToArray(),
            copied.Where(item => !item.IsDefault).Select(item => new StoreOffer(item.Id, item.Id, Currency, item.Price)).ToArray());
    }
    /// <summary>Gets copied immutable content in authored display order.</summary>
    public IReadOnlyList<ChessCosmeticDefinition> Definitions { get; }
    /// <summary>Gets the shared generic Commerce catalog; asset IDs remain opaque game-owned keys.</summary>
    public StoreCatalog Store { get; }
    /// <summary>Gets a known cosmetic or throws for an unknown identity.</summary>
    public ChessCosmeticDefinition Get(string id)
    {
        ProgressText.Require(id, nameof(id));
        return _lookup.TryGetValue(id, out var definition) ? definition : throw new ArgumentException("Unknown cosmetic identity.", nameof(id));
    }
    /// <summary>Gets the free default for an initialized slot.</summary>
    public ChessCosmeticDefinition GetDefault(ChessCosmeticSlot slot)
    {
        if (slot is not (ChessCosmeticSlot.Board or ChessCosmeticSlot.Pieces)) throw new ArgumentOutOfRangeException(nameof(slot));
        return Definitions.Single(item => item.IsDefault && item.Slot == slot);
    }
    /// <summary>Creates two free defaults and one earnable variant per slot. Assets stay in the consuming game.</summary>
    public static ChessCosmeticCatalog CreateStarter() => new(new[]
    {
        new ChessCosmeticDefinition("board.default", "Classic board", ChessCosmeticSlot.Board, 0, true),
        new ChessCosmeticDefinition("pieces.default", "Classic pieces", ChessCosmeticSlot.Pieces, 0, true),
        new ChessCosmeticDefinition("board.midnight", "Midnight board", ChessCosmeticSlot.Board, 100),
        new ChessCosmeticDefinition("pieces.ivory", "Ivory pieces", ChessCosmeticSlot.Pieces, 150)
    });
}

/// <summary>Immutable store availability and equipment state; views do not decide ownership or affordability.</summary>
public readonly struct ChessCosmeticSnapshot
{
    internal ChessCosmeticSnapshot(ChessCosmeticDefinition definition, bool owned, bool equipped, long balance)
    {
        Id = definition.Id; Title = definition.Title; Slot = definition.Slot; Price = definition.Price;
        IsDefault = definition.IsDefault; IsOwned = owned; IsEquipped = equipped;
        CanPurchase = !owned && !definition.IsDefault && balance >= definition.Price;
    }
    /// <summary>Gets the game-owned presentation identity.</summary>
    public string Id { get; }
    /// <summary>Gets its default title.</summary>
    public string Title { get; }
    /// <summary>Gets the affected slot.</summary>
    public ChessCosmeticSlot Slot { get; }
    /// <summary>Gets its local training-point cost.</summary>
    public long Price { get; }
    /// <summary>Gets whether this is a free initial choice.</summary>
    public bool IsDefault { get; }
    /// <summary>Gets permanent ownership.</summary>
    public bool IsOwned { get; }
    /// <summary>Gets whether this is the current choice for its slot.</summary>
    public bool IsEquipped { get; }
    /// <summary>Gets whether the current local balance and ownership permit purchase.</summary>
    public bool CanPurchase { get; }
}

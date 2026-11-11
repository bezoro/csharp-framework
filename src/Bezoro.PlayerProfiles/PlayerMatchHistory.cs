namespace Bezoro.PlayerProfiles;

/// <summary>Describes the outcome of an append attempt.</summary>
public enum MatchHistoryAppendResult
{
    /// <summary>A new record was appended.</summary>
    Added,
    /// <summary>The identical record was already present.</summary>
    AlreadyRecorded,
    /// <summary>The identifier already refers to different metadata.</summary>
    ConflictingRecord,
    /// <summary>The configured page capacity was reached.</summary>
    CapacityReached
}

/// <summary>A bounded, append-only page of one player's match history with constant-time duplicate checks.</summary>
/// <remarks>
/// Public operations are thread-safe and serialized per history. Records retain insertion order; storage grows only with appended records.
/// Full pages reject new records rather than evicting their duplicate ledger. The host pages persistent history separately.
/// </remarks>
public sealed class PlayerMatchHistory
{
    private readonly object _sync = new();
    private readonly List<PlayerMatchRecord> _records = new();
    private readonly Dictionary<string, int> _lookup = new(StringComparer.Ordinal);

    /// <summary>Creates an empty history page. Capacity must be positive.</summary>
    public PlayerMatchHistory(string playerId, int capacity = 256)
    {
        Identifier.Require(playerId, nameof(playerId));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        PlayerId = playerId;
        Capacity = capacity;
    }

    /// <summary>Gets the history owner.</summary>
    public string PlayerId { get; }
    /// <summary>Gets the maximum number of records in this page.</summary>
    public int Capacity { get; }
    /// <summary>Gets the number of appended records.</summary>
    public int Count { get { lock (_sync) return _records.Count; } }
    /// <summary>Gets an immutable record in insertion order.</summary>
    public PlayerMatchRecord this[int index] { get { lock (_sync) return _records[index]; } }

    /// <summary>Appends a valid record for this player, or reports a conflict, retry or capacity failure without mutation.</summary>
    public MatchHistoryAppendResult Append(PlayerMatchRecord record)
    {
        lock (_sync)
        {
            if (!record.IsValid || record.PlayerId != PlayerId)
                throw new ArgumentException("The initialized record must belong to this history's player.", nameof(record));
            if (_lookup.TryGetValue(record.MatchId, out int index))
                return _records[index] == record ? MatchHistoryAppendResult.AlreadyRecorded : MatchHistoryAppendResult.ConflictingRecord;
            if (_records.Count == Capacity) return MatchHistoryAppendResult.CapacityReached;
            _lookup.Add(record.MatchId, _records.Count);
            _records.Add(record);
            return MatchHistoryAppendResult.Added;
        }
    }

    /// <summary>Looks up a match without allocating. Unknown identifiers return false.</summary>
    public bool TryGet(string matchId, out PlayerMatchRecord record)
    {
        lock (_sync)
        {
            Identifier.Require(matchId, nameof(matchId));
            if (_lookup.TryGetValue(matchId, out int index))
            {
                record = _records[index];
                return true;
            }
            record = default;
            return false;
        }
    }

    /// <summary>Copies all records into caller-owned storage; too-short destinations throw before copying.</summary>
    public void CopyTo(Span<PlayerMatchRecord> destination)
    {
        lock (_sync)
        {
            if (destination.Length < _records.Count) throw new ArgumentException("The destination must fit every record.", nameof(destination));
            for (int index = 0; index < _records.Count; index++) destination[index] = _records[index];
        }
    }

    /// <summary>Allocates a coherent snapshot without requiring a separate count query.</summary>
    public PlayerMatchRecord[] CreateSnapshot()
    {
        lock (_sync) return _records.ToArray();
    }
}

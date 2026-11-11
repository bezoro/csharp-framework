# Bezoro.PlayerProfiles

Optional player-profile metadata, bounded match-history pages and pure Elo calculations. There are no Unity references, workers, clocks, network calls or storage dependencies. Both .NET 10 and .NET Standard 2.1 are supported.

```csharp
var profile = new PlayerProfile("alice", "Alice", portraitId: "portraits/knight");
var history = new PlayerMatchHistory(profile.PlayerId, capacity: 256);

var record = new PlayerMatchRecord(
    "match-42", profile.PlayerId, "bob", PlayerMatchOutcome.Win,
    completedAt: serverTime, gameMode: "rapid", isRated: true,
    replayReference: "replays/match-42");

if (history.Append(record) == MatchHistoryAppendResult.Added)
{
    var ratings = EloRatings.ApplyResult(
        new EloRating(1200), new EloRating(1250), record.Outcome);
    // Persist the match and both new ratings in the same authoritative transaction.
}
```

Identities, match identifiers and mode identifiers use ordinal, case-sensitive comparison. Display-name policy, authentication, portrait loading and account persistence belong to the host. Default profile and match-record structs are uninitialized and expose `IsValid == false`; histories reject default records. Records may identify a remote engine as an opponent using an application-defined identity.

`PlayerMatchHistory` retains insertion order and rejects conflicting metadata for an existing match identifier. An identical retry returns `AlreadyRecorded`, including after the page is full. Capacity failures leave all records and duplicate checks intact. The page grows with actual records; it does not reserve its entire maximum capacity at construction. `CopyTo` writes a snapshot into caller-owned storage. Restore a page by appending its persisted records in order.

A history is a bounded in-memory page, not a global match database or cross-page duplicate ledger. The host owns pagination and a persistent unique key on match identity. An account result, both players' rating changes, progression rewards and match-history entries should share the host's durable transaction boundary. Do not update rankings just because a client submitted an outcome.

`EloRatings.ExpectedScore` uses `1 / (1 + 10^((opponent - player) / 400))`. `ApplyResult` applies each player's independently configurable positive K factor to the same pre-match ratings and advances both counters. Defaults are 32. Values remain finite, extreme finite inputs cannot overflow the probability calculation, and exhausted counters or nonfinite updated ratings throw. Negative ratings are supported mathematically; the application chooses any rating floor and provisional-player policy. Elo functions allocate no managed storage and retain no match IDs, so the host must apply each authoritative result exactly once. These calculations do not reproduce Chess.com's rating system or the full FIDE rating regulations.

Immutable metadata and Elo calculations can be shared between threads. History append, lookup, indexing and copies are serialized per page. Concurrent identical retries append once. `CreateSnapshot()` allocates one coherent record array without a racy count-then-copy sequence; `CopyTo` remains allocation-free with caller-owned storage large enough for the page. Independent histories run on separate workers. Keep separate rating pools for modes such as bullet, blitz and rapid; there is no mandatory leaderboard allocation or background work.


# Bezoro.Progression

Optional, engine-independent quest, battle-pass, and story progress for .NET 10 and .NET Standard 2.1. The assembly has no other module or package dependencies. Unity projects consume its compiled DLL; no Unity types, frame updates, background tasks, reflection, or code generation are required.

Immutable content catalogs can be shared across players and threads. Each mutable progress instance represents one player in one selected catalog/season/revision. Public operations serialize through one private gate per instance, so concurrent updates retain every accepted amount and each claim has one winner. Independent players can run on separate workers. Callbacks and background tasks are not introduced; warmed data operations retain their allocation-free path.

## Quests

Quests require **all** their objectives. Each objective observes an application-defined counter and saturates at its target. A catalog indexes objectives by counter, so recording an event visits only matching objectives; unrelated quest nodes receive no work. When a catalog contains multi-objective quests, a per-quest remaining-objective counter detects completion in constant work, without rescanning the quest's other objectives when a target is reached. Catalogs containing only single-objective quests require no completion-counter array. Inactive, completed, and claimed quests do not accumulate further progress.

```csharp
using Bezoro.Progression;

var winThreeGames = new ProgressionId(10);
var gamesWon = new ProgressionId(100);
var coins = new ProgressionId(1000);

var quests = new QuestCatalog(new[]
{
    new QuestDefinition(winThreeGames,
        new[] { new QuestObjective(gamesWon, target: 3) },
        new[] { new RewardGrant(coins, quantity: 100) })
});
var progress = new QuestProgress(quests);
progress.TryActivate(winThreeGames);

// Feed authoritative completed-game outcomes, once per accepted event.
int newlyCompleted = progress.Record(gamesWon);
long wins = progress.GetObjectiveProgress(winThreeGames, objectiveIndex: 0);

if (progress.TryClaim(winThreeGames, out RewardBatch rewards))
{
    // Apply rewards and save the claimed state in the same host transaction.
}
```

Use distinct counter IDs for distinct filters, such as wins, ranked wins, and lessons passed. The host decides when a game is final and which counters it advances. The module does not infer filters from arbitrary gameplay objects, subscribe to event buses, or keep an event log.

The lifecycle is `Inactive → Active → Completed → Claimed`. Activating a quest is explicit, which permits the host to select daily quests, tutorials, or challenges. Repeated activation or claim returns false. Repeated event delivery **does** count repeatedly; deduplicate durable event IDs at the authoritative application boundary. For recurring quests, create progress for the selected recurrence and persist that recurrence identity alongside its catalog revision.

`Record` accepts positive `long` amounts and saturates safely, even for `long.MaxValue`. It returns how many quests became complete. `GetStatus` and `GetObjectiveProgress` provide read-only queries. Unknown quest IDs and malformed arguments throw; an unknown but valid event counter has no effect. Definitions reject duplicate quest IDs, duplicate counters within a quest, empty objectives, and malformed default values.

## Battle pass

Tiers use strictly increasing **cumulative** experience thresholds, with independent free and premium reward batches. Premium access can be granted after experience was earned; previously unlocked premium tiers remain claimable. Empty individual tracks cannot be claimed. A pass may contain zero tiers.

```csharp
var pass = new BattlePassDefinition(new[]
{
    new BattlePassTierDefinition(requiredExperience: 100,
        freeRewards: new[] { new RewardGrant(coins, 50) }),
    new BattlePassTierDefinition(requiredExperience: 300,
        freeRewards: new[] { new RewardGrant(coins, 100) },
        premiumRewards: new[] { new RewardGrant(new ProgressionId(2000)) })
});
var season = new BattlePassProgress(pass);
season.AddExperience(300);
season.GrantPremiumAccess(); // The host has already authorized the entitlement.

if (season.TryClaim(tierIndex: 1, track: BattlePassTrack.Premium, out RewardBatch reward))
{
    // Grant the corresponding application-owned cosmetic entitlement.
}
```

`AddExperience` returns the amount actually credited, saturating at `long.MaxValue`. `UnlockedTierCount` uses binary search; it does not traverse every tier. Claim lookup uses constant work. The host selects the season, authorizes experience/premium access, and decides when the season accepts claims; no wall-clock timer or payment client is retained.

## Story and lessons

Story stages form an acyclic prerequisite graph. All requirements must be met, and each may demand a particular best-star score. Stages without prerequisites start unlocked. Input order need not be topological. Catalog construction rejects cycles, missing dependencies, impossible scores, self-dependencies, and duplicate requirements. Graph validation is iterative, including deep campaigns.

```csharp
var movingPieces = new ProgressionId(1);
var capturing = new ProgressionId(2);
var story = new StoryProgress(new StoryCatalog(new[]
{
    new StoryStageDefinition(movingPieces),
    new StoryStageDefinition(capturing, prerequisites: new[]
    {
        new StoryPrerequisite(movingPieces, requiredStars: 2)
    })
}));

if (story.TryComplete(movingPieces, stars: 2, out StoryStageCompletion completion))
{
    bool firstCompletion = completion.IsFirstCompletion;
    bool nextLessonAvailable = story.IsUnlocked(capturing);
}
```

A completion improves the saved best score; equal/worse scores and locked stages return false without mutation. Valid scores are one through the stage's configured `MaximumStars` (one through 255); zero means unplayed. `StoryStageCompletion` distinguishes first completion from an improved score. The host validates gameplay before accepting completion and applies any first-completion rewards transactionally.

Stage IDs select application-owned content: instructional text, board/FEN setup, accepted moves, success conditions, voice, visuals, or another gameplay mode. This module handles unlocks and progress, so teaching content can evolve independently of the progression runtime. Stage unlock queries examine only that stage's immediate requirements and retain no update loop or duplicate unlock cache.

## Rewards, persistence, and authority

`ProgressionId` is an application-defined nonzero `uint`; default is invalid. IDs carry no player, catalog, revision, season, or reward-type identity. The host selects those namespaces and persists their context. `RewardGrant` is a readonly identifier/positive-quantity value. Map it to the host's currency, inventory, cosmetic, or access systems; no other framework module is mandatory. `RewardBatch` borrows immutable reward storage from a definition, so claims do not copy reward arrays. Retaining a batch retains that storage. Its default value is empty.

A successful claim changes in-memory claim state exactly once. **Durable exactly-once reward delivery requires an application transaction containing both the claim state and granted entitlements.** Granting externally and saving later can duplicate or lose rewards after a crash. These mutable objects do not implement database transactions, network authentication, purchase verification, event deduplication, or a persistence format.

Save using caller-owned buffers:

| State | Save | Restore |
| --- | --- | --- |
| Quests | `CopyStateTo(statuses, objectiveProgress)` | `TryRestore(statuses, objectiveProgress)` |
| Battle pass | Save `Experience`, `HasPremiumAccess`, and `CopyClaimStateTo(bytes)` | `TryRestore(experience, hasPremiumAccess, bytes)` |
| Story | `CopyStateTo(stars)` | `TryRestore(stars)` |

Arrays use catalog input order; quest objectives use definition order. Persist the catalog/schema/season identity separately, or explicitly migrate data when order or targets change. `BattlePassProgress.CopyStateTo(claims, out experience, out hasPremiumAccess)` captures all pass fields coherently; separate property queries and `CopyClaimStateTo` calls do not form a transaction. Caller-owned span inputs and outputs require exclusive access for the duration of each call. Restore validates the whole input before any mutation: quest lifecycle/counter consistency, battle-pass access/threshold/track claims, and completed-stage prerequisites. Wrong lengths or inconsistent data return false and preserve the current state. Restoring is an authoritative operation and can roll back claim flags; do not accept untrusted client snapshots as server state. Copy methods validate all destination capacities before writing and may receive larger buffers; restore requires exact lengths.

## Costs and validation

After construction, expected queries, event updates, completion, claims, copy, and restore allocate no managed storage. Exception creation, application snapshot arrays, and host reward delivery allocate as usual. Catalog construction copies definitions/rewards and builds shared indexes; immutable definition objects can be reused. Per-player quest storage is one `long` per objective and one status byte per quest, plus one remaining-objective `int` per quest when the catalog includes multi-objective quests; battle-pass storage is one claim byte per tier plus scalar experience/access; story storage is one best-score byte per stage. Each mutable owner also has one private synchronization gate; array/object headers are additional. The remaining-objective counters are derived on restore and need no persisted field. Zero-sized player arrays use shared empty storage. No event history, callbacks, task, polling interval, or asset references are retained.

Tests cover malformed content, overflow boundaries, claim replay, premium retroactivity, state restore atomicity, story cycles and a 25,000-stage chain, seeded event/claim models, and zero managed allocation in warmed operations on .NET 10. Unity Mono/IL2CPP performance must be measured in the consuming player.

```sh
dotnet test tests/Bezoro.Progression.Tests/Bezoro.Progression.Tests.csproj -c Release
```


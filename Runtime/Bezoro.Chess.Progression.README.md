# Bezoro.Chess.Progression

Local player progression composed from existing Framework modules. `PlayerProfile`, `PlayerMatchHistory` and `EloRatings` own profile/history/rating values; `QuestProgress` and `StoryProgress` own objective, claim and prerequisite rules; `CommerceAccount` owns currency, permanent ownership and purchase receipts. This optional module adds the chess application boundary and one atomic save. It contains no Unity references, loaded artwork, network services or global ranking.

```csharp
var options = new ChessProgressOptions();
ISaveStore store = new AtomicFileSaveStore(hostSaveDirectory, options.MaximumSaveBytes);
var campaign = ChessLearningCampaign.CreateFoundations();
var catalog = ChessProgressCatalog.CreateStarter(campaign);
var player = await ChessPlayerProgress.OpenAsync(
    store, "local-profile", new PlayerProfile("local-player", "Player"), catalog, options);

await player.UpdateProfileAsync("Ada", "portraits/knight");
await player.ClaimQuestAsync(ChessProgressIds.FirstVisit);
ChessPlayerSnapshot view = player.Snapshot;
// The host renders view.Profile, TrainingRating, History, Quests, Stages and Cosmetics.
```

Every published snapshot has already been persisted. Commands run serially within one owner, build a separate candidate, write through the supplied `ISaveStore`, and only then replace `Snapshot`. Cancellation or a precommit storage failure leaves the visible profile, quest claim, currency, ownership and equipment unchanged. No callback exceptions can turn a successful commit into an apparent command failure: the API returns a result and exposes immutable views instead of publishing arbitrary user handlers. The host owns the store, its platform directory and lifecycle; it must coordinate multiple application instances or processes using the same slot.

The starter catalog defines five one-time quests as ordinary data: open a profile (10 points), finish a lesson (25), finish a live training match (25), win one (50), and finish five distinct matches (150). Claims grant a total of 260 local training points. There is no paid currency, battle-pass requirement or automatic external reward. The first-visit objective is not account authentication. Match and lesson counters are derived by the Framework application, never supplied by Unity views.

Training Elo starts at 1200 with configurable K=32 and is separate from any online rating. Direct `RecordMatchAsync` calls must supply trusted finalized `ChessTrainingMatch` inputs with a stable logical match ID. Outcomes are derived from the rule session's terminal reason and player side. Identical retries do nothing; an ID reused for another result or opponent metadata throws. `Replay` inputs award neither history nor progression. The retained history rejects new entries at its configured capacity; it never removes duplicate identities to make space.

`ChessTrainingProgressRecorder` is an opt-in adapter for an actual `UciGameEngineSession`. Configure `Training`, `Replay`, `Lesson` or `Disabled` before starting/loading the corresponding match. The recorder captures UCI game IDs, actual move/undo events, terminal results and one completion timestamp. A first loaded terminal state and historical position loads are not new live matches. An existing UCI ID already in the player's history cannot award again after undo or recorder rebinding. Changed context invalidates the current logical game until a new game ID arrives. `SetContext` therefore belongs before `NewGameAsync` or `LoadMatchAsync`, not inside rendering updates.

```csharp
await using var recorder = new ChessTrainingProgressRecorder(
    session, player, opponentId: engineIdentity, opponentRating: configuredEngineElo,
    playerSide: 'w', mode: ChessTrainingProgressMode.Training);
await session.NewGameAsync();
// Normal session play emits typed events; the Framework queues the finalized result.
await recorder.DrainAsync();
```

The recorder starts a data worker only when a terminal record needs saving. It retains failed records and their original IDs/timestamps. `LastError` and `DrainAsync` surface failures; `RetryPendingAsync` retries the same captured facts. `Dispose` unsubscribes without abandoning already queued writes. Await `DisposeAsync` during shutdown, or stop observation and explicitly drain before destroying the application owner. Drain methods wait only for records the observer has captured. They do not pump the host's synchronization context. Let terminal callbacks arrive before switching game identity or disposing when their result must be retained. Storage work is not performed through `async void` handlers. No callback loads engine assets or invokes Unity.

`TryGetUnlockedLesson` uses the campaign's existing `StoryCatalog`. `CompleteLessonAsync` accepts only a `ChessLessonCompletion` issued by `GuidedLessonPlayer`, including both scripted phases and goal puzzles. The lesson ID and definition fingerprint must match the current campaign and the stage must be unlocked. Each first completion earns one completion star; no new grading system is invented. Resetting, replaying or improving an attempt does not duplicate lesson counters or claimed rewards. Boss/fundamental metadata remains in the immutable Study campaign.

Default board and piece cosmetics are always owned and initially equipped. Their IDs are `board.default` and `pieces.default`. The starter paid variants are `board.midnight` (100 points) and `pieces.ivory` (150 points). `PurchaseCosmeticAsync` delegates spending and ownership to `CommerceAccount` with stable transaction IDs. `EquipCosmeticAsync` preserves independent typed board and piece slots and rejects unowned/wrong-slot choices. Both share the atomic application save. `Cosmetics` projects ownership, equipment and affordability; a view should not reimplement those checks. Cosmetic IDs are opaque keys for the consuming game's asset registry: materials, sprites, textures, meshes and prefabs stay in that game.

The bounded schema-1 payload retains profile metadata, distinct live-match facts, verified lesson receipts, ordered quest/purchase identities, equipment and a checked balance snapshot. Restore replays those facts through the existing Framework owners, validates the exact player/catalog/rating policy, rejects conflicting identities and verifies balances against the claim/purchase ledger. The persistence envelope provides corruption detection, not authentication or protection against a user rolling back their own offline files. Catalog/rating-policy changes require an explicit migration; unknown schemas, corrupt saves and foreign profiles are never silently reset or overwritten. Localize presentation in the host by stable content IDs; changing authored teaching definitions changes their fingerprint.

Both .NET 10 and .NET Standard 2.1 are supported. Focused tests use the real atomic file store and local rule-session events to verify restore, cancellation/failure boundaries, duplicate matches and lessons, exactly-once rewards, typed cosmetic equipment, purchases and recorder drainage. These managed tests do not certify any game engine's rendering or player filesystem.


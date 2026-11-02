using System;
using Bezoro.Chess.Networking;
using Bezoro.Chess.Presentation;
using Bezoro.Chess.Study;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.Commerce;
using Bezoro.PlayerProfiles;
using Bezoro.Progression;

namespace Bezoro.UnityApiSmoke
{
    public static class ChessProductConsumer
    {
        // A small host-side composition. The game owns authentication, transactions and rendered assets.
        public static long ExerciseDataApis()
        {
            using var match = new AuthoritativeChessMatch(Guid.NewGuid(), PlayableMatchSetup.Standard);
            ChessMatchSnapshot start = match.GetSnapshot();
            var move = new ChessMatchCommand(start.MatchId, start.Revision, 1, ChessMatchCommandKind.Move, "e2e4");
            ChessMatchCommandReceipt receipt = match.Submit(ChessPlayerSeat.White, move);
            if (receipt.Status != ChessMatchCommandStatus.Accepted)
                throw new InvalidOperationException("The local authority rejected a legal move.");
            var presentation = ChessBoardPresentation.FromMatch(receipt.Snapshot.State);
            var e4 = new ChessSquare(4, 3);
            if (presentation.GetPieceSymbol(e4) != 'P' ||
                (presentation.GetHighlights(e4) & ChessSquareHighlights.LastMove) == 0)
                throw new InvalidOperationException("Board projection lost the last move.");
            if (ChessBoardCoordinates.GetColumn(e4, ChessBoardOrientation.Black) != 3)
                throw new InvalidOperationException("Black orientation failed.");

            ChessStudy study = Pgn.Parse("1.e4 e5 *");
            study.Redo();
            study.PlaySan("c5");
            study.Undo();
            if (study.Current.VariationCount != 2 || Pgn.Parse(Pgn.Write(study)).NodeCount != 4)
                throw new InvalidOperationException("Study variations were lost.");
            var lesson = new ChessLesson(Fen.Default, new[] { new[] { "e2e4" } });
            if (!lesson.StartAttempt().TryPlay("e2e4").Completed)
                throw new InvalidOperationException("Scripted lesson did not complete.");

            var questId = new ProgressionId(1);
            var wonGame = new ProgressionId(2);
            var coins = new ProgressionId(3);
            var quests = new QuestProgress(new QuestCatalog(new[] {
                new QuestDefinition(questId, new[] { new QuestObjective(wonGame, 1) },
                    new[] { new RewardGrant(coins, 100) })
            }));
            quests.TryActivate(questId);
            quests.Record(wonGame);
            if (!quests.TryClaim(questId, out _))
                throw new InvalidOperationException("Quest reward was not claimable.");
            var catalog = new StoreCatalog(
                new[] { new SkinDefinition("marble", "sprites/marble", "prefabs/marble") },
                new[] { new StoreOffer("marble-offer", "marble", "coins", 50) });
            var account = new CommerceAccount(catalog, "alice");
            account.CreditCurrency("quest-claim-1", "coins", 100);
            if (!account.Purchase("purchase-1", "marble-offer", DateTimeOffset.UtcNow).Succeeded || !account.TryEquipSkin("marble"))
                throw new InvalidOperationException("Skin purchase or equipment failed.");
            var rating = EloRatings.ApplyResult(new EloRating(1200), new EloRating(1200), PlayerMatchOutcome.Win);
            if (rating.FirstPlayer.Value <= 1200 || rating.SecondPlayer.Value >= 1200)
                throw new InvalidOperationException("Rating pair was not updated consistently.");
            var profile = new PlayerProfile("alice", "Alice");
            var history = new PlayerMatchHistory(profile.PlayerId, capacity: 8);
            history.Append(new PlayerMatchRecord("game-1", "alice", "bob", PlayerMatchOutcome.Win,
                DateTimeOffset.UtcNow, "casual", replayReference: "games/game-1.pgn"));
            return account.GetBalance("coins");
        }
    }
}

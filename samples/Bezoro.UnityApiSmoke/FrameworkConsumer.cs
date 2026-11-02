using System;
using Bezoro.Chess.UCI.API;
using Bezoro.Chess.UCI.API.Types;
using Bezoro.Chess.UCI.Protocol.API;
using Bezoro.Chess.UCI.Protocol.API.Types;
using Bezoro.Core.Types;
using Bezoro.Core.Types.Pool;
using Bezoro.ECS.Services;
using Bezoro.ECS.Types;
using Bezoro.Events.Services;
using Bezoro.GameSystems.HealthSystem.Types;
using Bezoro.Logging.Types;
using Bezoro.TypingSystem.Types;
using Bezoro.TypingSystem.Utilities;

namespace Bezoro.UnityApiSmoke
{
    public static class FrameworkConsumer
    {
        public static int ExerciseDataApis()
        {
            var chessOptions = new UciCoordinatorOptions();
            if (chessOptions.PonderThreads <= 0)
                throw new InvalidOperationException("Chess coordinator defaults were not initialized.");
            // Initializers must also work when a .NET host selects the framework's .NET target.
            var poolOptions = new PoolOptions { MaxCapacity = 1 };
            if (poolOptions.MaxCapacity != 1 || !poolOptions.EnableAsyncWait || !poolOptions.ValidateOnRent)
                throw new InvalidOperationException("Pool options initializer failed.");
            using (var pool = new ObjectPool<object>(() => new object(), poolOptions))
            {
                object item = pool.Rent();
                if (pool.TryRent(out _)) throw new InvalidOperationException("The configured pool capacity was ignored.");
                pool.Return(item);
            }
            using var bus = new EventBus();
            int damage = 0;
            bus.Subscribe<Damage>(context => damage += context.Data.Amount);
            bus.Enqueue(new Damage(3));
            if (bus.FlushQueued(maxEvents: 1) != 1 || damage != 3)
                throw new InvalidOperationException("Bounded event dispatch failed.");

            using var world = new World(new WorldConfig
            {
                EntityCapacity = 8, ComponentTypeCapacity = 8, QueryResultCapacity = 8,
                ChunkCapacity = 8, CommandCapacity = 16, CommandPayloadCapacityPerType = 16,
                MaxDegreeOfParallelism = 1
            });
            var actor = world.Spawn(new Health(max: 100, current: 100));
            ref Health health = ref world.Write<Health>(actor);
            health.Current -= (uint)damage;
            if (world.Read<Health>(actor).Current != 97)
                throw new InvalidOperationException("Explicit component access failed.");

            var words = new ArrayWordProvider(new[] { "go" });
            if (!words.TryGetNextWord(out ReadOnlyMemory<char> target))
                throw new InvalidOperationException("Word consumption failed.");
            var progress = TypingState.Initial;
            var options = new TypingValidatorOptions { IgnoreCase = true };
            foreach (char input in "GO")
            {
                var result = TypingValidator.ValidateInput(target.Span, progress.Position, input, options);
                if (!result.IsCorrect) throw new InvalidOperationException("Typing validation failed.");
                progress = progress.WithCorrect();
            }
            if (!progress.IsComplete(target.Length) || words.TryGetNextWord(out _))
                throw new InvalidOperationException("Typing completion failed.");

            // This public constructor must also work with Unity's C# 9 compiler.
            var payload = new LogPayload(LogLevel.Info, "ready", "ready", "") { Timestamp = DateTime.UnixEpoch };
            if (payload.Message != "ready" || payload.Level != LogLevel.Info)
                throw new InvalidOperationException("Log payload construction failed.");

            using var match = new UciPlayableMatchSession();
            if (match.Refresh().LegalMoves.Length != 20)
                throw new InvalidOperationException("Local chess setup failed.");
            match.ApplyMove("e2e4");
            if (match.Refresh().LegalMoves.Length != 20)
                throw new InvalidOperationException("Local chess move failed.");

            var clientOptions = new UciClientOptions { MaxCapturedPrincipalVariations = 16 };
            if (clientOptions.MaxCapturedPrincipalVariations != 16)
                throw new InvalidOperationException("Client options initializer failed.");
            using var game = new UciGameEngineSession(options: new UciCoordinatorOptions { EnableBackgroundAnalysis = false });
            game.NewGameAsync().GetAwaiter().GetResult();
            if (game.LegalMoves.Count != 20)
                throw new InvalidOperationException("Local chess facade failed.");
            game.LoadSaveAsync(match.CaptureSave()).GetAwaiter().GetResult();
            game.UndoAsync().GetAwaiter().GetResult();
            game.MakeMoveAsync("d2d4").GetAwaiter().GetResult();
            match.LoadSaveAsync(game.CaptureSaveAsync().GetAwaiter().GetResult()).GetAwaiter().GetResult();
            if (match.CurrentState.MoveHistory.Length != 1 || match.CurrentState.MoveHistory[0].Move != "d2d4")
                throw new InvalidOperationException("Chess saves were not interchangeable across the public APIs.");
            return checked((int)world.Read<Health>(actor).Current);
        }

        private readonly struct Damage
        {
            internal Damage(int amount) { Amount = amount; }
            internal int Amount { get; }
        }
    }
}

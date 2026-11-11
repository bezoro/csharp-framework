# Bezoro.Chess.Progression.Tests

Application tests use `AtomicFileSaveStore` in isolated temporary directories. Fault/blocking decorators exercise precommit failure, cancellation and publication order; they retain the real serializer and storage envelope. Recorder tests use real local `UciGameEngineSession` moves, checkmate, loads and undo without substituting event payloads or needing an external engine.

Bounded-admission tests hold writes in flight and fail storage persistently, verifying finite pending capacity, retained rejection identity, callback delivery outside the gate, subscriber isolation, stale acknowledgment rejection and ordered host recovery. Successful accepted-write retries cannot silently erase admission failure.

Clock callback regressions verify that cross-thread recorder reads complete without holding its gate and that reentrant context invalidation/disposal cannot publish an obsolete match.

Run `dotnet test tests/Bezoro.Chess.Progression.Tests/Bezoro.Chess.Progression.Tests.csproj`. The production assembly also targets .NET Standard 2.1; build that target before Unity package staging. No Unity or global user preferences are accessed by these tests.

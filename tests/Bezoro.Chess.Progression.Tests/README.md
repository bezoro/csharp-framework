# Bezoro.Chess.Progression.Tests

Application tests use `AtomicFileSaveStore` in isolated temporary directories. Fault/blocking decorators exercise precommit failure, cancellation and publication order; they retain the real serializer and storage envelope. Recorder tests use real local `UciGameEngineSession` moves, checkmate, loads and undo without substituting event payloads or needing an external engine.

Run `dotnet test tests/Bezoro.Chess.Progression.Tests/Bezoro.Chess.Progression.Tests.csproj`. The production assembly also targets .NET Standard 2.1; build that target before Unity package staging. No Unity or global user preferences are accessed by these tests.

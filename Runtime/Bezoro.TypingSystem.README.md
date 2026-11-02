# Bezoro.TypingSystem

Engine-independent word provisioning, character validation, state transitions, callbacks, and performance metrics for typing games and training tools.

## Main types

| Type | Purpose |
| --- | --- |
| `TypingValidator` | Validates one input character against a target span without allocating. |
| `TypingResult` | Describes the expected/input characters, position, length, and validation status. |
| `TypingState` | Tracks immutable position, correct-input, and mistake counts for one target. |
| `TypingValidatorOptions` | Configures case handling, callbacks, and optional metrics collection. |
| `TypingMetrics` | Tracks accuracy, correct characters per minute, mistakes, faults, and elapsed time. |
| `IWordSource` | Atomically consumes words from a typing-session source. |
| `ArrayWordProvider` | Stores words, consumes them once, and provides concrete mutation operations. |
| `WordProviderFileExtensions` | Appends words from a text file to an `ArrayWordProvider`. |

## Quick start

```csharp
using Bezoro.TypingSystem.Types;
using Bezoro.TypingSystem.Utilities;

var target = "hello";
var state = TypingState.Initial;
var metrics = new TypingMetrics();
var options = new TypingValidatorOptions { IgnoreCase = true, Metrics = metrics };

// A game UI would call this once per submitted character.
foreach (char input in "HELLO")
{
    var result = TypingValidator.ValidateInput(target.AsSpan(), state.Position, input, options);
    state = result.IsCorrect ? state.WithCorrect() : state.WithMistake();
}

Console.WriteLine(state.IsComplete(target.Length)); // True
Console.WriteLine($"Accuracy: {metrics.Accuracy:P0}");
```

## API reference

### Validation

`TypingValidator.ValidateInput` returns `Match`, `Completed`, `Mismatch`, `EmptyTarget`, or `PositionOutOfRange`. Positions and lengths use `int`, matching spans and strings. Negative or past-end validation indices return a recoverable `PositionOutOfRange` fault.

Create `TypingResult` values through the public `TypingResult.Match`, `TypingResult.Mismatch`, `TypingResult.Completed`, `TypingResult.EmptyTarget`, and `TypingResult.PositionOutOfRange` factories. The constructor is private. Flags derive from status, and factories reject positions that contradict their status. Use `Completed` for the final character and `Match` for an earlier character.

### State and metrics

Use `TypingState.WithCorrect()` and `TypingState.WithMistake()` to derive the next immutable state. Starting at `TypingState.Initial`, each correct input advances `Position`; `CorrectCount` is the same accepted-character count. Restore progress with `new TypingState(position: 20, mistakeCount: 3)`, without a second independently writable correct count. `IsComplete(targetLength)` becomes true when all characters have been accepted. Stop submitting input for that target after completion; the next position is beyond its final character. Empty targets are validation faults and do not complete this workflow.

Pass a `TypingMetrics` instance through `TypingValidatorOptions` to record each validation automatically. Metrics reject `default(TypingResult)` before updating counts. A recorded result updates metrics first, then invokes `OnValidated`, then invokes its status callback. Completion invokes `OnCompleted` rather than `OnMatch`. A throwing callback prevents later callbacks in that validation; metrics are already recorded. Metrics and callbacks run synchronously on the validation caller's thread. Callbacks can allocate or throw; the validator does not catch their exceptions. Shared metrics and UI callbacks require external synchronization or dispatch to the engine thread.

### Word providers

Use `IWordSource.TryGetNextWord` as the consumption contract. It atomically reports exhaustion and returns empty memory when no word remains. `ArrayWordProvider` owns the concrete `AddWord`, `AddWords`, `RemoveWord`, `ClearWords`, and `WordCount` mutation and inspection operations.

`ArrayWordProvider` enumerates its constructor input once, supporting sources that can only be consumed once. Empty initial sources are valid. Use `new ArrayWordProvider()` for later population; null string entries are rejected. Empty strings can be consumed or removed like other stored words. Removing a word uses swapback storage and can change unread order. Removing a consumed word preserves the remaining unread words without replaying consumed ones. `WordCount` counts stored words, including those already consumed; it is not the unread count.

`ArrayWordProvider` supports concurrent consumers: each stored word is reserved by at most one successful call. Exhausted reads do not advance the cursor, so words appended afterward remain available. Mutations, including file loading, must be externally synchronized with all other operations on the provider. Returned memory refers to an owned immutable string and remains valid after the provider is changed.

Load one word per line during caller-controlled setup through the focused file adapter:

```csharp
using Bezoro.TypingSystem.Extensions;
using Bezoro.TypingSystem.Types;

var provider = new ArrayWordProvider();
provider.LoadWordsFromFile(path);
```

Consume through `IWordSource`, mutate the concrete provider, and load files through `WordProviderFileExtensions.LoadWordsFromFile`. `AddWords` enumerates directly; enumeration failure keeps words already appended. File loading reads every line before modifying the provider, so a file-read failure leaves it unchanged.

## Behavior

- Validation consumes one UTF-16 code unit per call. Case-insensitive validation uses invariant casing for that code unit; it does not normalize accents or combine grapheme clusters. Hosts own the mapping from text-input events to submitted code units.
- Callbacks distinguish matches, completion, mismatches, and validation faults.
- Empty targets and out-of-range positions return fault results instead of throwing.
- File loading is synchronous, reads all lines before appending any words, and is intended for caller-controlled setup paths.

## Runtime notes

- Validation accepts spans so per-keystroke checks do not require string allocation.
- Result and state values are immutable; callers own session-level progression.
- Metrics are optional and kept outside the validator's static core.
- The module targets .NET 10 and .NET Standard 2.1. Unity 6 uses the precompiled .NET Standard DLLs; consume them from Unity scripts using syntax supported by Unity's compiler.

## Migration

- Replace `IWordProvider` with the consumption-only `IWordSource`. Remove `HasMoreWords` checks and consume through `TryGetNextWord`.
- Replace `AddWordsFromFile` with `LoadWordsFromFile` from the extensions namespace.
- Replace direct `TypingResult` construction with its named factories. Contradictory flags and positions cannot be supplied.
- Replace `new TypingState(position, correctCount, mistakeCount)` with `new TypingState(position, mistakeCount)`. Progress now has one accepted-character count. Positions and mistake counts are `int`; byte and ushort limits no longer apply. `ArrayWordProvider.WordCount` is also an `int`, and empty providers are valid.

## Build and validation

Use the SDK selected by [global.json](https://github.com/bezoro/framework/blob/e7472a00169f392e3724e170581fe384c0e024ef/global.json):

```bash
dotnet build src/Bezoro.TypingSystem/Bezoro.TypingSystem.csproj -c Release
dotnet test tests/Bezoro.TypingSystem.Tests/Bezoro.TypingSystem.Tests.csproj -c Release -f net10.0
```

See [the TypingSystem tests](https://github.com/bezoro/framework/blob/e7472a00169f392e3724e170581fe384c0e024ef/tests/Bezoro.TypingSystem.Tests/README.md) for completion, single-pass source, removal, and concurrent-consumer coverage.


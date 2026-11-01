# Bezoro.Chess.UCI.Protocol.Tests
Unit and integration tests for `Bezoro.Chess.UCI.Protocol`.

## Test Areas
| Folder                  | Source Mirror                                         | Description                                                                                      |
|-------------------------|-------------------------------------------------------|--------------------------------------------------------------------------------------------------|
| `API/Types`             | `src/Bezoro.Chess.UCI.Protocol/API/Types`             | FEN parsing, search result parsing, PV parsing, and metadata types.                              |
| `API/Common/Extensions` | `src/Bezoro.Chess.UCI.Protocol/API/Common/Extensions` | Consumer-facing formatting, move-analysis, and move-normalization extensions.                    |
| `Domain`                | `src/Bezoro.Chess.UCI.Protocol/Domain`                | `ProcessUciTransport`, `UciEngineClient`, command emission, lifecycle, and integration behavior. |
| `Domain/Common/Helpers` | `src/Bezoro.Chess.UCI.Protocol/Domain/Common/Helpers` | UCI parsing and validation helpers.                                                              |
| `Internal`              | `src/Bezoro.Chess.UCI.Protocol/Internal`              | Internal coordination, cancellation-generation, and background-worker behavior.                 |
| `TestHelpers`           | Test-only                                             | Shared builders, fake transports, fixtures, and constants.                                       |
| `TestResources/Engine`  | Test-only                                             | Bundled Stockfish executable/resources used by integration tests.                                |

## Quick Start
```bash
dotnet test tests/Bezoro.Chess.UCI.Protocol.Tests/Bezoro.Chess.UCI.Protocol.Tests.csproj
```

## Useful Commands
```bash
dotnet test tests/Bezoro.Chess.UCI.Protocol.Tests/Bezoro.Chess.UCI.Protocol.Tests.csproj --filter "FullyQualifiedName~UciEngineClient"
dotnet test tests/Bezoro.Chess.UCI.Protocol.Tests/Bezoro.Chess.UCI.Protocol.Tests.csproj --filter "FullyQualifiedName~ProcessUciTransport"
dotnet test tests/Bezoro.Chess.UCI.Protocol.Tests/Bezoro.Chess.UCI.Protocol.Tests.csproj --filter "Category=Integration"
```

## What These Tests Guarantee
- Standard UCI commands are serialized correctly.
- Handshake lines (`id`, `option`, `uciok`, `readyok`) are parsed and surfaced correctly.
- Typed info messages, standalone principal variations, and completed search transcripts share the same casing, spacing, score-replacement, and terminal-field grammar.
- Failed principal-variation parses return an initialized empty move collection rather than a default immutable array.
- Transport lifecycle is safe across start, stop, disposal, and backpressure conditions.
- Engine-specific escape hatches (`d`, `go perft 1`) continue to work for supported engines, but playable-match session tests now also cover the protocol-owned local FEN/legal-move path that does not depend on those extensions.
- The playable-match contract stays stable for canonical events, rich move payloads, promotion request/response, claimable draws, clocks, and engine-vs-engine loop helpers; deterministic clock tests verify that undo restores retained values and stages, restarts the retained turn unpaused without historical-time debit or a stale pause marker, and respects the clock-history origin of loaded matches.
- Event ordering, draw-offer policies, controlled-move fallback policies, and batch request processing remain consumer-facing tested behavior.
- Move classifications publish one immutable completed batch without blocking enqueue callers, and canceled worker generations cannot overwrite replacement results.
- Move classification and position analysis remain FIFO and single-threaded across cancellation generations; stale success, fault, and cancellation outcomes cannot mutate replacement work.
- Local chess rules include six perft reference positions through depth four and retain king-safety constraints, special-move ordering and application, clock and castling-right updates, repetition identity, material adjudication, and batch/single classification equivalence while sharing one parsed position and parsing each classification batch at most once.
- Game review tests cover expected-points thresholds, mover perspective, ratings passed to custom models, exact-score requirements, engine-safety validation, N+1 history-preserving searches, terminal positions, concurrency, cancellation, and missing WDL.
- Transport process fixtures exercise equivalent stdout, stderr UTF-8, exit, and teardown-failure paths on Windows and POSIX hosts.

Regression coverage also checks that configured engine clients add no refresh/load searches, canceled synchronous requests publish no events, malformed move subsets and histories perform no truncated command writes, and typed fields reject embedded command separators. Analysis tests cover cache eviction, pending-budget atomicity, terminal disposal, joined workers, and bounded retained PVs. Real child-process tests fill stdin beyond its OS pipe capacity and require disposal to release both the process and blocked writer. Extreme centipawn and mate values remain valid display inputs.

Validation uses real Stockfish 19 through `BEZORO_STOCKFISH_PATH` on non-Windows hosts. Runtime builds target both net10.0 and netstandard2.1; integration tests run on net10.0.

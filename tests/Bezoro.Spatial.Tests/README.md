# Spatial tests

The tests check inclusive bounds, finite coordinate validation, explicit cell budgets, zero capacity, and float extremes. Mutation tests remove every position in a bucket and move the swapped final entry between buckets. Seeded sequences perform 18,000 operation steps and compare circle/rectangle results with an independent brute-force dictionary by identity, position, and payload. Duplicate and missing hits fail that comparison. Extreme-center regressions verify that cell size does not change circle inclusion when coordinate subtraction loses small terms to rounding.

An undersized destination must remain unchanged and report the exact required hit count. Other cases verify stale/foreign handles, reference release, simultaneous read-only queries, concurrent insertion/removal, queries during movement and bucket changes, exception-safe read/write ownership, and zero allocation for warmed queries and ordinary mutations. Tests use the host managed runtime; they do not establish a player/export runtime's behavior or timing.

```sh
dotnet test tests/Bezoro.Spatial.Tests/Bezoro.Spatial.Tests.csproj -c Release
```

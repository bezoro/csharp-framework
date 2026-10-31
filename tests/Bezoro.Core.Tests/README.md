# Bezoro.Core.Tests

Unit tests for `Bezoro.Core` primitives, extensions, helpers, and utility types.

## Types

| Type/Area           | Description                                                                                                           |
|---------------------|-----------------------------------------------------------------------------------------------------------------------|
| `Helpers/*Tests`    | Mirrors `src/Bezoro.Core/Helpers` (converter, array, comparers, exception, and validation helper behavior).           |
| `Types/*Tests`      | Mirrors `src/Bezoro.Core/Types` (grid, result, singleton, try helpers, percent, ranges, vectors, pools, collections). |
| `Utilities/*Tests`  | Mirrors `src/Bezoro.Core/Utilities` (`StringTags` processing and replacement behavior).                               |
| `Extensions/*Tests` | Mirrors `src/Bezoro.Core/Extensions` (arrays, collections, enums, spans, memory, strings, and type extensions).       |

## Run

```bash
dotnet test tests/Bezoro.Core.Tests/Bezoro.Core.Tests.csproj -c Release -f net10.0 --verbosity minimal
```

## Coverage notes

- Tests are deterministic and focus on behavior contracts and edge cases.
- High-churn suites are split into focused files (`Types/Try*Tests`, `Extensions/StringExtensions*Tests`) for maintainability.
- Assertions use xUnit and FluentAssertions.
- Pool tests protect the owned-capacity invariant: failed creation reservations are released, and owned items rejected
  during rent validation or return reset are removed and discarded exactly once.
- `PooledObjectHandle<T>` tests cover copied handles to ensure that all struct copies share disposal state and return their
  value exactly once.
- `SwapbackArray<T>` lookup tests exercise `TryGetIndex(T, out uint)` as the canonical lookup kernel; the legacy nullable
  index helper remains covered for compatibility.
- Compatibility tests preserve obsolete expression guards, lifecycle boolean overloads, and formatting convenience shims
  while guiding callers to the canonical APIs.

## Toolkit coverage

- Binary vectors, overlap, truncation, prefix limits, cursor preservation and IEEE bit round-trips.
- Bit-set tail masks, self operations, enumeration invalidation, portable scans and boolean reference models.
- Sorted span bounds against linear models, reverse comparers and reference-clearing removals in slices.
- Signed division/modulo against BigInteger, scalar wrapping against decimal, finite interpolation extremes and damping near underflow.
- Integer magnitude overflow, negative nearest-multiple rounding and unrepresentable results.

These utility suites include warm allocation checks. Such checks do not include construction, exception paths, caller-owned comparer behavior, or application serialization.

## Conventions

- Test method names follow `Method_WhenCondition_ShouldExpectation`.
- Each test class targets a focused source type or behavior slice.
- Disposable resources in tests use explicit ownership/disposal patterns.

Tests use the .NET 10 SDK selected by [global.json](../../global.json). They run without Unity or Godot and do not replace validation in engine player builds.

## Regression coverage

- `GridSafetyTests` covers dimension overflow, invalid span dimensions and storage, row-aliasing coordinates, pooled initialization, and access after disposal.
- `SwapbackArraySafetyTests` covers replacement at the logical end, self-appending, partial enumeration failures, and indirectly aliased LINQ sources. Capacity-boundary tests exercise arithmetic without allocating multi-gigabyte arrays.
- `ObjectPoolLifecycleSafetyTests` covers disposal with pending timed and indefinite renters, infinite timeouts, duplicate owned returns, reset and validation failures, failed prewarming, and continuing cleanup after a discard failure.
- `ObjectPoolDuplicateOwnershipTests` covers foreign-object ownership, preserving active rentals when the pool is full, and duplicate returns during reset or discard and after disposal.
- Validation-helper tests require valid objects to pass the obsolete null guard and preserve named parameter details for null values.
- `CoreBoundaryRegressionTests` covers the UInt32/float boundary, packed color layouts, strict CSS input, CSS output and allocation-free color formatting.
- `SingletonLifecycleTests` covers cross-thread constructor guards, reverse-order scope closure, duplicate ownership, reset of nested scopes and disposal once.
- `ObjectPoolAllocationTests` covers warmed reuse with async waiting enabled or disabled, immediate timed rents, configuration bounds, failed creation with an awaiting renter and cleanup after a throwing discard callback.
- `ObjectPoolRentReentrancyTests` prevents validation and rent callbacks from publishing their in-progress rental to a second consumer.
- `CodeWriterTests` covers mixed text/line indentation, idempotent scopes, scope order and class/namespace boundaries.
- `LegacyUtilitiesRegressionTests` covers null array slots, count-only collections, finite extreme and tiny ranges, relative distance overflow, cancellation propagation and removed transient allocations.
- `TicketLockTests` covers callback failure and ticket wrap in checked builds.

Run the same contracts with checked integer arithmetic:

```bash
dotnet test tests/Bezoro.Core.Tests/Bezoro.Core.Tests.csproj -c Release -p:CheckForOverflowUnderflow=true
```

Concurrency tests use bounded waits to expose ownership errors. Passing them is evidence for those scenarios, not a proof covering every possible thread interleaving.

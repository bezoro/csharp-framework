# Bezoro.Spatial

Finite 2D rectangles and a fixed-capacity uniform-grid index for points. It supports proximity selection, targeting, interest queries, and point-based placement checks without Unity or Godot types. `Vector2` comes from `System.Numerics`. Runtime targets are .NET 10 and .NET Standard 2.1; Core supplies generation-checked handles.

## Add, move, and query

```csharp
using System.Numerics;
using Bezoro.Spatial;

var index = new PointSpatialIndex<int>(
    worldBounds: new Bounds2D(new Vector2(-128), new Vector2(128)),
    cellSize: 8,
    capacity: 1024,
    maximumCells: 1024);

var actor = index.Add(new Vector2(10, 20), value: 42);
index.TryMove(actor, new Vector2(12, 20));

// Allocate once per query owner and reuse. Separate readers need separate buffers.
var hits = new SpatialHit<int>[1024];
var result = index.QueryCircle(new Vector2(12, 22), radius: 5, hits);
if (result.IsSuccess)
{
    for (int i = 0; i < result.WrittenCount; i++)
        SelectActor(hits[i].Value);
}

index.Remove(actor);
```

`TryAdd` returns false and a default handle for outside-world positions or exhausted entry capacity. `Add` throws for those cases. Non-finite positions throw on both insertion APIs and on `TryMove`, even if another argument would otherwise fail. A finite out-of-world move, stale handle, default handle, or foreign handle returns false without changing entries. `TrySetValue` changes an entry's payload without changing its position or identity. `TryGet` returns a copied `SpatialHit<T>`; reference payloads remain references to application-owned objects.

Each handle is scoped to this owner. It survives moves and unrelated removals. Removal and `Clear` invalidate it; exhausting a slot's generation retires that slot. `AvailableCapacity` reports remaining reusable slots. Handles are process-local identities and should not be serialized as durable game IDs. Removed/cleared reference payloads are released; the index does not dispose them.

## Geometry and output contracts

`Bounds2D` uses inclusive minimum and maximum coordinates. Shared edges intersect. Zero-area query rectangles are valid, and the default rectangle is the origin point. Index world bounds require positive width and height. Both world corners are accepted, including the maximum corner in the final cell.

Rectangles and circles use closed boundaries. A zero-radius circle matches coincident points. Circle centers and radii must be finite, and radii must be nonnegative. A point must pass both the circle's inclusive bounding rectangle and its squared-distance comparison. Both calculations use binary64 arithmetic, so subtracting or squaring finite binary32 coordinates cannot overflow. Checking the same bounding rectangle during filtering and cell pruning keeps inclusion independent of cell size, including when extreme coordinate differences lose small terms to rounding. Comparisons still follow floating-point rounding; this is not an exact-real arithmetic geometry package.

Queries return each matching entry once. Hit order is unspecified and can change after insertion, movement, or removal. Results are copied snapshots; a returned handle may become stale after a later mutation.

An undersized destination returns `DestinationTooSmall`, zero `WrittenCount`, and the exact `RequiredCapacity` without modifying any element. On success, only the first `WrittenCount` elements are overwritten. The unused tail retains its previous contents, including any reference payloads; clear that tail when the application needs to release those references. Empty results succeed, including with an empty span. Queries never return partial results.

## Capacity, memory, and cost

Construction checks `ceil(width / cellSize) × ceil(height / cellSize)` against `maximumCells` before allocating entries or buckets. Dimension arithmetic uses binary64 rather than overflowing binary32 endpoint subtraction. The grid uses four bytes per cell. A zero-capacity index computes its logical grid dimensions but allocates no bucket array.

Entry storage uses Core's `SlotMap` plus point, payload, and bucket links. It is approximately `capacity × (sizeof(T) + 40)` bytes, with possible additional value-type alignment padding and object/array headers. For an `int` payload, that is 44 bytes per entry plus cell storage. The index never grows, creates no task per operation, and retains no per-query cache or scratch arrays.

Insertion, movement, payload replacement, and removal use constant-time entry/bucket updates. Removal swaps the final dense entry into the free position and repairs its bucket links while preserving its handle. `Clear` clears live entries and all allocated cell heads, retaining their storage.

A query visits the cells in its clipped bounding rectangle and tests their points. It costs O(visited cells + candidate points), or O(all cells + all points) for a world-sized query. A destination with room for every live point needs one traversal. Smaller destinations first count matches to guarantee complete output, then traverse again only when the result fits. Select a cell size appropriate to expected point density and query radius; a dense cell can still contain every point.

## Concurrency and engine use

Queries and `TryGet` calls can run concurrently, including while other threads request mutations. A per-index reader/writer gate grants mutations exclusive access and gives waiting writers priority over new readers. Each query keeps its read ownership across both sizing and copying, so results describe one consistent index state. Synchronization happens once at the API boundary, never per point or cell. The gate uses managed monitor state and requires no disposal or background workers.

Each caller supplies distinct destination memory. Payload references remain application-owned and are not made thread-safe by the index. Individual property reads are consistent observations rather than a combined transaction. Separate indexes execute independently.

Convert Unity/Godot vectors at the adapter boundary and keep scene-object access on the engine thread. This module indexes points, not extended colliders, and does not implement collision response, sweep tests, nearest-neighbor ranking, unbounded worlds, or physics integration. Rectangle/circle results are exact for their stored point positions under the documented floating-point comparisons.

## Validation

See the [tests](../../tests/Bezoro.Spatial.Tests/README.md) for differential and lifecycle coverage, and the [benchmarks](../../benchmarks/Bezoro.Spatial.Benchmarks/README.md) for reusable workloads.

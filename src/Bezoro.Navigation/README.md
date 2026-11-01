# Bezoro.Navigation

Weighted two-dimensional grid navigation for data-only simulations. The module has no Unity, Godot, ECS, or third-party runtime dependency. It targets .NET 10 and .NET Standard 2.1; Unity 6 consumes the precompiled assembly.

## Find a route

```csharp
using Bezoro.Navigation;

// Row-major costs. Zero blocks a cell; positive values weight entering it.
ushort[] costs =
{
    1, 8, 1, 1,
    1, 0, 0, 1,
    1, 1, 1, 1
};
var grid = new NavigationGrid(4, 3, costs);
var pathfinder = new GridPathfinder(grid.CellCount);
var routeBuffer = new GridPoint[grid.CellCount];

var result = pathfinder.FindPath(grid, new GridPoint(0, 0), new GridPoint(3, 0),
    routeBuffer, new GridPathOptions(GridMovement.EightWay));
if (result.Status == GridPathStatus.Found)
{
    for (int i = 0; i < result.WrittenLength; i++)
    {
        GridPoint cell = routeBuffer[i];
        // Convert cell to a world position in the engine adapter.
    }
}
```

Allocate the map, pathfinder, and destination buffer during setup and reuse them. `NavigationGrid` copies input costs; modifying the caller's array after construction does not change the map. `new NavigationGrid(width, height)` creates a uniform walkable map. Coordinate values may be negative, but a search requires both endpoints inside its grid. `default(GridPoint)` is `(0, 0)`.

`Contains` and `IsWalkable` return false for negative coordinates, including in assemblies compiled with checked integer arithmetic. Searches reject out-of-grid endpoints before changing their destination.

## Costs and topology

- Orthogonal edges cost `1000 * destinationCellWeight`.
- Diagonal edges cost `1414 * destinationCellWeight`. This is a defined integer metric, not an exact Euclidean distance.
- The start cell's weight is excluded. Both endpoints are included in the output route.
- Four-way movement is the default. Eight-way movement forbids diagonal corner cutting unless `AllowCornerCutting` is true. With cutting disabled, both orthogonal neighbors of a diagonal edge must be walkable.
- Weights are `ushort` values. Zero is blocked; `1..65535` are valid weights. Dimensions must be positive and their product must fit in an `int`.

A* uses Manhattan distance for four-way movement and octile distance for eight-way movement, scaled by the map's smallest positive weight. Both heuristics are admissible and consistent for these edge costs. Different entering-cell weights make reverse edge costs potentially different. The search finds the minimum weighted cost, which can involve more steps than the shortest geometric route.

Costs and scores use `ulong`. With at most `int.MaxValue` cells and weights at most `65535`, simple paths and heuristic scores fit in that type. Ties prefer the lowest total estimated cost, then the largest cost already traveled, then the lowest row-major cell index. Identical inputs produce the same route in this implementation. There is no floating-point calculation in the search.

## Outcomes and budgets

| Status | Output behavior |
| --- | --- |
| `Found` | Writes the complete route. `WrittenLength` and `RequiredPathLength` are equal. |
| `DestinationTooSmall` | Leaves the destination unchanged. Reports the complete required point count and route cost. |
| `Unreachable` | Leaves the destination unchanged. No route cost or required size is reported. |
| `Canceled` | Leaves the destination unchanged. No partial route is published. |
| `ExpansionLimitReached` | Leaves the destination unchanged. Another query can retry with a different budget. |

`TotalCost` is nullable and has a value only when a route has been found, including `DestinationTooSmall`. Check `Status` before consuming an existing buffer. `HasPath` does not imply that points were written.

An expansion means examining one node's outgoing edges. The goal is not expanded. `MaxExpandedNodes = 0` permits only a trivial path where start equals goal. A budget of one can find an adjacent goal. The default options value and an omitted options argument both select four-way movement, no corner cutting, and `int.MaxValue` expansions.

After argument and workspace ownership validation, outcome precedence is:

1. A canceled token returns `Canceled`, including for a blocked or trivial position.
2. A blocked endpoint returns `Unreachable`, including when start equals goal.
3. A walkable start equal to goal needs one output point, costs zero, and consumes zero expansions. An empty destination returns `DestinationTooSmall`.
4. Other positions are searched within the expansion budget. Reaching the goal succeeds without consuming another expansion.

Cancellation is checked before each node expansion and during route sizing. Once the final route copy begins, that bounded copy completes and the query returns `Found`; cancellation arriving during publication does not turn a complete route into a canceled partial result. The publication work is bounded by the map area and, for a found route, by the expansion budget plus one point.

The search is synchronous and does not resume a canceled or budget-limited query. Destination-too-small results require another query with a larger buffer if the route is needed. Application-level queues can decide when to retry.

## Ownership and memory

The immutable map can be shared between workers. A pathfinder owns mutable search arrays and rejects overlapping or reentrant searches with `InvalidOperationException`. Give each concurrently active worker its own pathfinder and destination buffer. The module does not create threads, tasks, or callbacks. Use the game's existing bounded worker scheduler for parallel agent requests.

The workspace's array payload is 32 bytes per capacity cell: two `ulong` scores and four 32-bit index/stamp arrays. The map retains two bytes per cell. A destination `GridPoint` uses two `int` coordinates. These counts exclude object/array headers and runtime-dependent alignment. Capacity is fixed and must cover the complete map area, even for a small expansion budget; oversized maps are rejected rather than growing buffers during gameplay.

An indexed binary heap holds each open cell once and performs decrease-key in logarithmic time. Visit stamps avoid clearing all search arrays between queries. The stamp array is cleared when its 32-bit generation wraps, once per roughly four billion nontrivial searches. There are no managed allocations per valid query after construction. Invalid-argument exceptions allocate normally.

For Unity or Godot, build immutable navigation data from scene data on the appropriate host thread. Run searches against that data, then apply world positions, animation, rendering, and scene changes through the host's normal thread rules. The library does not make engine APIs safe to call from workers. Maps are snapshots; construct a new one when terrain changes. This module does not implement physics collision, navigation meshes, asynchronous request ownership, path smoothing, or dynamic obstacle avoidance.

## Validation and profiling

The tests compare 600 searches on 200 seeded weighted/blocked maps against a separate Dijkstra implementation across all movement/corner policies. They validate route steps and costs, endpoint cases, defaults, output capacity, cancellation, expansion budgets, deterministic ties, visit-generation wrap, workspace reuse, shared-map workers, and a zero-allocation steady-state sample.

```sh
dotnet test tests/Bezoro.Navigation.Tests/Bezoro.Navigation.Tests.csproj -c Release
dotnet run -c Release --project benchmarks/Bezoro.Navigation.Benchmarks -- --filter '*GridPathfindingBenchmarks*'
```

Benchmarks reuse maps and working storage on open, weighted, and striped-maze maps at 64 and 256 cells per side. Construction is excluded. Benchmark the target player and representative terrain before choosing worker counts, workspace capacity, or frame budgets. .NET tests and benchmark results do not establish Unity IL2CPP or Godot export compatibility.

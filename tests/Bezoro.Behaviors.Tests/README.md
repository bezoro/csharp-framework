# Behavior composition tests

These tests exercise optional adapters through their public APIs. Definitions are reused; each mutable child runtime is exclusively owned by one graph.

Coverage includes tree/flat-machine/statechart nesting with one shared visit budget, linear progress through deep continuations, reactive parents, round-robin parallel bindings, state changes while suspended, stale completion signals, terminal mapping and immediate resource cleanup, reentry, constructor failures, sparse state ranges, faulty callbacks, reentrancy, and reuse after a failed Stop. Signal regressions attempt ancestor Stop/Tick and sibling Dispatch from descendant guards and lifecycle callbacks, checking resource cleanup, unchanged sibling tokens, and reservation release after exceptions. A separate allocation check includes warm Start/Tick/Stop operations. Constructor comparisons verify that inactive declared states and wide unvisited branches do not increase adapter execution storage.

Named-action builders execute a child across one-visit quanta without calculating preorder offsets. Invalid handles or duplicate bindings leave existing children available. A failed composition-limit check permits a corrected Build; success transfers ownership and consumes the builder. A layout assertion checks that runtime bindings retain only their integer and child reference.

A forced-collection check keeps the resulting runtime alive while proving its expression, compilation resolver, and builder can be collected before execution.

```sh
dotnet test tests/Bezoro.Behaviors.Tests -c Release
dotnet test tests/Bezoro.Behaviors.Tests -c Release -p:CheckForOverflowUnderflow=true
```

Host callback behavior, engine scene access, actual asynchronous job cancellation, and target-player exports remain application responsibilities. The token checks demonstrate rejection of late results rather than stopping external work automatically.

# Behavior tree tests

Tests cover memory and reactive control flow, condition short-circuiting, finite repeats and copied subtrees, lifecycle events, activation invalidation, callback faults and reentrancy, depth-bounded storage and cooperative visits.

A separate bounded interpreter generates 1,200 terminal trees and compares results, visit counts and callback order under small random budgets. Deep paths run with a one-visit budget to expose starvation. Counters ensure completed memory branches and inactive siblings are not reevaluated. An allocation check exercises 100,000 warm updates through a struct handler.

A budget-slicing property generates 1,000 trees with Running actions, reactive control, repeats, and copied subtrees. It compares callback traces at one visit and 10,000 visits per update while context changes only through action ticks. Finite executions must reach the same result; intentionally unbounded reactive executions compare bounded prefixes. This property complements the terminal-tree interpreter and is not a separate implementation of tree semantics.

Compilation-resolver cases locate named actions after changing prefixes, distinguish separate expressions sharing an action ID, and reject default/foreign handles and missing or ambiguous expressions. Copied compiled subtrees do not silently inherit construction-expression identity.

```sh
dotnet test tests/Bezoro.BehaviorTrees.Tests -c Release
dotnet test tests/Bezoro.BehaviorTrees.Tests -c Release -p:CheckForOverflowUnderflow=true
```

Independent runners share definitions in concurrent tests. A single runner remains an externally serialized owner. Managed tests do not launch engine editors or exports.

Concurrency coverage blocks a real callback on a dedicated worker, verifies overlapping lifecycle operations are rejected before effects, and checks admission release after invalid calls. Shared immutable definitions retain independent execution ownership.

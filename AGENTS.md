# AGENTS.md

## Package Version Source

Context: Unity package staging and CI release builds.
Rule: Resolve package versions with `scripts/Get-PackageVersion.ps1` from SemVer Git tags; use `BEZORO_PACKAGE_VERSION` or `-PackageVersion` only for explicit local overrides.
Reason: `unity-package/package.json`, release assembly metadata, and CI publishing should stay on one version source.

## Public API readability

Context: All public APIs throughout the framework.

Rules:

- Make public APIs as intuitive as possible for human readers. Usage should be clear, declarative, and follow a natural logical sequence, almost like reading a book.
- Choose names, parameters, and composition patterns that express the caller's intent. Avoid cryptic abbreviations, unexplained positional values, and surprising behavior.
- Make ownership, lifecycle, side effects, and failure behavior clear in the public contract. Keep implementation and storage details internal whenever callers do not need them.
- Internal implementations may be complex when that complexity materially improves performance or memory use. Keep that complexity behind the API and document the invariants needed to maintain it.
- Preserve the pay-for-use principle. A readable API must not introduce unnecessary allocations, processing, or mandatory abstractions.

Reason: Consumers should be able to understand what their code does by reading its public API calls, even when the implementation uses advanced optimizations.

## Breaking changes

Breaking changes are always welcome when they result in a better codebase. Prefer a clearer, simpler, more robust or more efficient design over preserving backward compatibility. Document affected contracts and migration steps, and mark breaking changes using Conventional Commits and Semantic Versioning.

## Engine-independent concurrency

Design the framework for safe concurrent use and useful parallel execution without depending on an engine thread, ambient synchronization context or global dispatcher. Unity and Godot adapters own engine-specific thread affinity; preserve parallel data processing and marshal only operations that require the engine thread.

- Prefer immutable shared definitions, readonly results and coherent snapshots. Stateful shared services must synchronize complete operations, including reads, claims, lifecycle changes and snapshot publication.
- Keep execution workspaces and borrowed storage under explicit exclusive ownership. Reject unsupported overlapping operations atomically before effects; a lock around a getter cannot protect a reference or span after it escapes.
- State callback execution, ownership, reentrancy, cancellation and disposal contracts explicitly. Protect internal state without invoking arbitrary user callbacks under blocking locks. Thread-safe bookkeeping does not make caller-owned values or callbacks thread-safe.
- Bound parallel work and retained queues; observe and join owned work during shutdown. Never create tasks or marshal threads merely to make a synchronous operation look concurrent.
- Preserve the pay-for-use principle: no mandatory engine dispatcher, task allocation, polling or per-element synchronization in pure calculations and exclusively owned kernels. Verify concurrency invariants with deterministic regressions and measure material performance changes in Release builds.

## Implementation economy

Maximize useful impact with the least code necessary. Prefer simple, cohesive solutions; every additional abstraction, dependency and line of code must justify its cost. Remove duplication and unnecessary machinery while preserving readability, correctness, performance and explicit ownership. Measure economy by the complete solution, including integration and maintenance, rather than by terse syntax or line count alone.

## Commits and versioning

Commit cohesive, validated increments as work progresses. Use Conventional Commits and Semantic Versioning: identify breaking changes with `!` or a `BREAKING CHANGE` footer, and classify releases according to their public API impact. Resolve package versions through the canonical version script described above; keep release notes and migration guidance current.

## Pull request visibility

For changes intended for integration, open a draft pull request as soon as the first coherent implementation is pushed, so the user can follow progress. Keep its scope, description and validation status current as commits arrive. Mark it ready for review when implementation and the relevant checks are complete.

Every pull request description must start with a concise summary of what it does, followed by a `## Changes` section listing all changes and a `## Verification` section listing every verification performed and its result. Clearly identify pending checks separately from completed verification.

## CI economy

Preserve CI quota by consolidating verification. Working-branch commits and pushes should not automatically run CI; prefer automatic validation on integration into `main`, including merges. Use focused local checks during development. Trigger an additional manual CI run only when it has a concrete purpose, such as consolidated pre-merge validation or diagnosing a platform-specific failure. Avoid redundant runs of unchanged code while retaining the checks required for integration and publication.

## Immutable Unity package metadata

Include stable .meta files for every staged asset and directory, including generated XML documentation. Validate missing, malformed and duplicate GUIDs before publication; Unity cannot repair metadata in a Git package cache. Preserve existing GUIDs when rebuilding.

## Redirected process readers

Keep blocking reads from known synchronous process-pipe backends off the shared ThreadPool. `ReadLineAsync` can still block pool workers on Mono. Preserve genuine asynchronous stream paths, bounded backpressure, cancellation and joined shutdown. Stderr user callbacks must remain ordered on the ThreadPool. Do not address writer starvation by increasing global pool minima or protocol timeouts.

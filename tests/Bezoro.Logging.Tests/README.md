# Bezoro.Logging.Tests

Characterization tests for the public `Bezoro.Logging` behavior that must remain stable while the implementation is simplified.

## Isolation

`Logger` settings and `Logger.OnLog` are process-global. Test parallelization is disabled, each test restores all mutable public settings through `LoggerSettingsScope`, and every event subscription is removed in a `finally` block.

## Coverage

- Public payload content, formatting, styles, caller information, and file metadata
- Formattable-string collection formatting
- Enabled, minimum-level, and muted-category filtering, including messages whose formatter must never execute when filtered
- Exception, inner-exception, stack-trace, custom-message, and caller details
- Initial, changed, and repeated stage dispatch ordering
- Async-context nested ordering and restoration, sibling-branch isolation, and idempotent scope disposal
- Provider evaluation boundaries, immutable settings snapshots, and synchronous caller-thread dispatch

The allocation benchmark project records both the ordinary and nested async-context paths.

## Run

```powershell
dotnet test tests/Bezoro.Logging.Tests/Bezoro.Logging.Tests.csproj -c Release -f net10.0 --verbosity minimal
```

Tests use the .NET 10 SDK selected by [global.json](../../global.json). They run without Unity or Godot and do not replace validation in engine player builds.

The test project explicitly defines `DEBUG`, so conditional log call sites execute during Release tests. This does not change the Release library's `PerformanceTimer` behavior. Filtering tests establish that the logger avoids formatting hidden messages; call-site argument construction is still the caller's cost when `DEBUG` is defined.

Regression coverage includes disposal in inherited async flows, rejected out-of-order scope disposal, no-subscriber work suppression, success severity filtering, null message formatting, and validated time-window grouping.

# Bezoro.Build.Tests

Integration tests for repository build and packaging scripts.

## Scope

| Area                      | Description                                                                 |
|---------------------------|-----------------------------------------------------------------------------|
| `PackageVersionScriptTests` | Exercises SemVer precedence, validation, metadata, and Git-tag version resolution. |
| `UnityPackageScriptTests` | Verifies runtime dependency staging, conflicts, missing artifacts, literal path names, and protection against source/Git paths and symbolic-link ancestors. |
| `UnityPackagePublishScriptTests` | Publishes to an isolated local bare repository, checking idempotence, Conventional Commits, artifact replacement, and retained history. |
| `UnityRuntimeCompatibilityTests` | Executes real .NET Standard consumer IL against .NET 10 runtime assemblies and checks the init-modifier type forwarders. |

## Quick Start

```bash
dotnet test tests/Bezoro.Build.Tests/Bezoro.Build.Tests.csproj
```

## Design Notes

- Tests create temporary Git repositories so version behavior is isolated from the working tree.
- The package version resolver is invoked through PowerShell to cover the same entry point used by local packaging and CI.
- Fixtures locate the source checkout even when `--artifacts-path` puts test binaries outside it. The symbolic-link fixture runs on Unix; Windows builds exercise the shared path guard, but do not prove junction behavior.
- Publication uses a credential-free remote URL rewritten to the local bare repository. Temporary process configuration supplies authentication without embedding a token in command arguments or Git config files.
- Runtime compatibility checks retain the target mismatch that exposed a MissingMethodException in an init setter. Consumer compilation alone cannot detect that load-time signature failure.

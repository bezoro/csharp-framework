# Public API Baselines

These snapshots describe the compiled public API of each framework assembly. The exporter groups target frameworks with identical signatures and keeps target-specific snapshots when they differ. It includes constructors, members, generic constraints, defaults and the nullable signatures emitted by the compiler.

Build the Release solution and check the baselines:

```sh
dotnet build bezoro.framework.sln -c Release
pwsh -NoProfile -File scripts/Export-PublicApi.ps1 -Check
```

For intentional API changes, run the script without `-Check` after rebuilding and review the diff. The exporter is a development tool and is not a dependency of the runtime or UPM package. The snapshots make declarative contract changes visible. Review behavior, binary compatibility and migration separately.

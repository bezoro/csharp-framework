param([switch]$Check)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$tool = Join-Path $PSScriptRoot 'PublicApiSnapshot/PublicApiSnapshot.csproj'
$arguments = @('run', '--project', $tool, '--configuration', 'Release', '--', $repositoryRoot)
if ($Check) { $arguments += '--check' }
& dotnet @arguments
exit $LASTEXITCODE

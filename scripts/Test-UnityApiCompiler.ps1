param(
    [Parameter(Mandatory)]
    [string]$CompilerPath,
    [Parameter(Mandatory)]
    [string]$ReferenceAssemblyPath,
    [string]$FrameworkBinaryRoot = 'artifacts/unity-package/Runtime',
    [string]$OutputPath = 'artifacts/old-csharp-consumer'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
function Resolve-RepositoryPath([string]$path) {
    [IO.Path]::GetFullPath($path, $repositoryRoot)
}

$compiler = Resolve-RepositoryPath $CompilerPath
$referenceRoot = Resolve-RepositoryPath $ReferenceAssemblyPath
$frameworkRoot = Resolve-RepositoryPath $FrameworkBinaryRoot
$outputRoot = Resolve-RepositoryPath $OutputPath
if (-not (Test-Path $compiler -PathType Leaf)) { throw "Compiler DLL not found: $compiler" }
$references = @(Get-ChildItem $referenceRoot -File -Filter '*.dll' | Sort-Object Name | ForEach-Object FullName)
if ($references.Count -eq 0) { throw 'No .NET Standard reference assemblies were found.' }
foreach ($name in @('Core', 'Simulation', 'Navigation', 'Persistence', 'Inventory', 'StateMachines', 'Spatial', 'Conditions', 'Statecharts', 'BehaviorTrees', 'Behaviors', 'ECS', 'Events', 'GameSystems', 'Logging', 'TypingSystem', 'Chess.UCI.Protocol', 'Chess.UCI', 'Chess.Study', 'Chess.Presentation', 'Chess.Networking', 'Commerce', 'PlayerProfiles', 'Progression')) {
    $dll = Join-Path $frameworkRoot "Bezoro.$name.dll"
    if (-not (Test-Path $dll -PathType Leaf)) { throw "Framework DLL not found: $dll" }
}

# Include the staged dependency closure required by public API signatures.
$references += @(Get-ChildItem -LiteralPath $frameworkRoot -File -Filter '*.dll' | Sort-Object Name | ForEach-Object FullName)

New-Item $outputRoot -ItemType Directory -Force | Out-Null
$outputAssembly = Join-Path $outputRoot 'Bezoro.UnityApiSmoke.dll'
$response = Join-Path $outputRoot 'consumer.rsp'
# Match the SDK's default suppression for reference-assembly version unification.
# .NET Standard 2.0 dependencies legitimately bind to the supplied 2.1 profile.
$arguments = @('/nostdlib+', '/target:library', '/langversion:9.0', '/nullable:enable', '/warnaserror+', '/nowarn:1701,1702')
$arguments += '/out:"' + $outputAssembly + '"'
$arguments += @($references | ForEach-Object { '/reference:"' + $_ + '"' })
foreach ($file in @('FoundationConsumer.cs', 'ToolkitConsumer.cs', 'BehaviorConsumer.cs', 'FrameworkConsumer.cs', 'ChessProductConsumer.cs')) {
    $arguments += '"' + (Join-Path $repositoryRoot "samples/Bezoro.UnityApiSmoke/$file") + '"'
}
[IO.File]::WriteAllLines($response, $arguments, [Text.UTF8Encoding]::new($false))

# An older compiler can run on the installed SDK runtime without changing project compiler settings.
$previousRollForward = $env:DOTNET_ROLL_FORWARD
try {
    $env:DOTNET_ROLL_FORWARD = 'LatestMajor'
    & dotnet $compiler -noconfig "@$response"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally { $env:DOTNET_ROLL_FORWARD = $previousRollForward }
Write-Host "C# 9 consumer compiled with the supplied compiler: $outputAssembly"

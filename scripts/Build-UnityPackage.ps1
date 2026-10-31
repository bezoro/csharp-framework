param(
	[string]$Configuration = "Release",
	[string]$OutputPath = "artifacts/unity-package",
	[string]$PackageVersion = $env:BEZORO_PACKAGE_VERSION
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$templateRoot = Join-Path $repositoryRoot "unity-package"
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath))
$runtimeRoot = Join-Path $outputRoot "Runtime"
$versionScript = Join-Path $PSScriptRoot "Get-PackageVersion.ps1"

if (-not (Test-Path -LiteralPath $templateRoot)) {
	throw "Unity package template not found at '$templateRoot'."
}

if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
	$PackageVersion = & $versionScript -RepositoryPath $repositoryRoot
}
else {
	$PackageVersion = & $versionScript -RepositoryPath $repositoryRoot -VersionOverride $PackageVersion
}

# Staging owns only a dedicated directory under artifacts, never source or Git data.
$relativeOutput = [System.IO.Path]::GetRelativePath($repositoryRoot, $outputRoot)
$outputParts = $relativeOutput -split '[\\/]'
if ($outputParts.Length -lt 2 -or $outputParts[0] -ne 'artifacts' -or $outputParts -contains '.git') {
    throw "OutputPath must be a staging directory under the repository's artifacts directory."
}
$currentOutputPart = $repositoryRoot
foreach ($part in $outputParts) {
    $currentOutputPart = Join-Path $currentOutputPart $part
    if (Test-Path -LiteralPath $currentOutputPart) {
        $item = Get-Item -LiteralPath $currentOutputPart -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'OutputPath must not traverse symbolic links or junctions.'
        }
    }
}

if (Test-Path -LiteralPath $outputRoot) {
	Remove-Item -LiteralPath $outputRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
foreach ($templateItem in Get-ChildItem -LiteralPath $templateRoot -Force) {
    Copy-Item -LiteralPath $templateItem.FullName -Destination $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null

$manifestPath = Join-Path $outputRoot "package.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifest.version = $PackageVersion
$manifest | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $manifestPath -Encoding utf8

$legacyPluginsRoot = Join-Path $runtimeRoot "Plugins"
if (Test-Path -LiteralPath $legacyPluginsRoot) {
	Remove-Item -LiteralPath $legacyPluginsRoot -Recurse -Force
}

# These assemblies belong to Unity's .NET Standard 2.1 profile. Shipping NuGet
# copies introduces duplicate definitions in Unity, so use the host's versions.
$hostAssemblies = @(
    'Microsoft.CSharp.dll', 'System.Buffers.dll', 'System.Memory.dll',
    'System.Numerics.Vectors.dll', 'System.Threading.Tasks.Extensions.dll'
)
$stagedAssemblies = @{}

# Runtime READMEs are flattened beside DLLs. Resolve their source-relative links
# against the reviewed source revision rather than nonexistent package folders.
$documentationRevision = 'main'
try {
    $revision = & git -C $repositoryRoot rev-parse --verify HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $revision -match '^[0-9a-fA-F]{40}$') {
        $documentationRevision = $revision
    }
}
catch { }
$documentationBase = "https://github.com/bezoro/framework/blob/$documentationRevision/"

function Copy-RuntimeReadme([string]$SourcePath, [string]$DestinationPath) {
    # Reparse an explicit file URI so fragments remain anchors instead of literal path characters.
    $sourceUri = [Uri]::new(([Uri]::new([IO.Path]::GetFullPath($SourcePath))).AbsoluteUri)
    $content = Get-Content -LiteralPath $SourcePath -Raw
    $content = [regex]::Replace($content, '\]\((?:<(?<angle>[^>]+)>|(?<target>[^)\s]+))(?<title>\s+"[^"]*")?\)', [System.Text.RegularExpressions.MatchEvaluator] {
        param($match)
        $target = if ($match.Groups['angle'].Success) { $match.Groups['angle'].Value } else { $match.Groups['target'].Value }
        if ($target.StartsWith('#') -or $target.StartsWith('/') -or
            $target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:') { return $match.Value }
        $resolved = [Uri]::new($sourceUri, $target)
        if (-not $resolved.IsFile) { return $match.Value }
        $relative = [IO.Path]::GetRelativePath($repositoryRoot, $resolved.LocalPath).Replace('\', '/')
        if ($relative -eq '..' -or $relative.StartsWith('../')) { return $match.Value }
        $escaped = ($relative -split '/' | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
        return '](' + $documentationBase + $escaped + $resolved.Query + $resolved.Fragment + $match.Groups['title'].Value + ')'
    })
    Set-Content -LiteralPath $DestinationPath -Value $content -Encoding utf8
}

$projects = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "src") -Recurse -Filter "*.csproj" |
	Where-Object { $_.BaseName -ne "Bezoro.ECS.SourceGen" } |
	Sort-Object FullName

foreach ($project in $projects) {
	$assemblyName = $project.BaseName
	$buildOutputDirectory = Join-Path $project.Directory.FullName "bin\$Configuration\netstandard2.1"
	$readmePath = Join-Path $project.Directory.FullName "README.md"

	if (-not (Test-Path -LiteralPath $buildOutputDirectory)) {
		throw "Expected build output for '$assemblyName' at '$buildOutputDirectory'. Run 'dotnet build bezoro.framework.sln -c $Configuration' first."
	}

	if (-not (Test-Path -LiteralPath $readmePath)) {
		throw "Expected README for '$assemblyName' at '$readmePath'."
	}

	$artifactPath = Join-Path $buildOutputDirectory "$assemblyName.dll"
	if (-not (Test-Path -LiteralPath $artifactPath)) {
		throw "Missing runtime assembly '$artifactPath'. Rebuild before staging."
	}

	foreach ($assembly in Get-ChildItem -LiteralPath $buildOutputDirectory -Filter '*.dll' -File) {
		if ($assembly.Name -in $hostAssemblies) { continue }
		$hash = (Get-FileHash -LiteralPath $assembly.FullName -Algorithm SHA256).Hash
		if ($stagedAssemblies.ContainsKey($assembly.Name) -and $stagedAssemblies[$assembly.Name] -ne $hash) {
			throw "Conflicting runtime dependency '$($assembly.Name)' in build outputs. Clean and rebuild the solution."
		}
		$stagedAssemblies[$assembly.Name] = $hash
		Copy-Item -LiteralPath $assembly.FullName -Destination (Join-Path $runtimeRoot $assembly.Name) -Force
	}

	Copy-RuntimeReadme $readmePath (Join-Path $runtimeRoot "$assemblyName.README.md")
	$xmlPath = Join-Path $buildOutputDirectory "$assemblyName.xml"
	if (Test-Path -LiteralPath $xmlPath) {
		Copy-Item -LiteralPath $xmlPath -Destination (Join-Path $runtimeRoot "$assemblyName.xml") -Force
	}
}

Write-Host "Unity package $PackageVersion staged at $outputRoot"

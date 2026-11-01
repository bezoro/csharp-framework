param(
	[string]$PackagePath,
	[string]$Branch = "upm"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
	throw "PackagePath is required."
}

$packageRoot = [System.IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path $packageRoot)) {
	throw "Package path '$packageRoot' does not exist."
}

if ([string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
	throw "GITHUB_TOKEN must be set."
}

if ([string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY)) {
	throw "GITHUB_REPOSITORY must be set."
}

$publishRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("bezoro-upm-" + [System.Guid]::NewGuid().ToString("N"))
$remoteUrl = "https://github.com/$($env:GITHUB_REPOSITORY).git"

# Keep credentials out of remote URLs, command arguments, and temporary Git config files.
$previousConfigCount = $env:GIT_CONFIG_COUNT
$configCount = 0
if (-not [string]::IsNullOrEmpty($previousConfigCount) -and
    (-not [int]::TryParse($previousConfigCount, [ref]$configCount) -or $configCount -lt 0 -or $configCount -gt [int]::MaxValue - 2)) {
    throw 'GIT_CONFIG_COUNT must be a nonnegative integer with room for two publication entries.'
}
$previousGitConfig = @{}
$credentials = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("x-access-token:$($env:GITHUB_TOKEN)"))
for ($offset = 0; $offset -lt 2; $offset++) {
    $keyName = "GIT_CONFIG_KEY_$($configCount + $offset)"
    $valueName = "GIT_CONFIG_VALUE_$($configCount + $offset)"
    $previousGitConfig[$keyName] = [Environment]::GetEnvironmentVariable($keyName)
    $previousGitConfig[$valueName] = [Environment]::GetEnvironmentVariable($valueName)
    [Environment]::SetEnvironmentVariable($keyName, 'http.https://github.com/.extraheader')
    # Empty first entry clears inherited checkout headers before adding this token.
    $value = if ($offset -eq 0) { '' } else { "AUTHORIZATION: basic $credentials" }
    [Environment]::SetEnvironmentVariable($valueName, $value)
}
$env:GIT_CONFIG_COUNT = ($configCount + 2).ToString([Globalization.CultureInfo]::InvariantCulture)

try {
	& git ls-remote --exit-code --heads $remoteUrl $Branch *> $null
	$branchExists = $LASTEXITCODE -eq 0

	if ($branchExists) {
		& git clone --depth 1 --branch $Branch $remoteUrl $publishRoot
		if ($LASTEXITCODE -ne 0) {
			throw "Failed to clone branch '$Branch'."
		}
	}
	else {
		New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
		Push-Location $publishRoot
		try {
			& git init --initial-branch=$Branch
			if ($LASTEXITCODE -ne 0) {
				throw "Failed to initialize publish repository."
			}

			& git remote add origin $remoteUrl
			if ($LASTEXITCODE -ne 0) {
				throw "Failed to configure publish repository remote."
			}
		}
		finally {
			Pop-Location
		}
	}

	Push-Location $publishRoot
	try {
		Get-ChildItem -Force |
			Where-Object { $_.Name -ne ".git" } |
			Remove-Item -Recurse -Force

		Copy-Item (Join-Path $packageRoot "*") $publishRoot -Recurse -Force

		& git config user.name "github-actions[bot]"
		& git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
		& git add --all

		& git diff --cached --quiet
		if ($LASTEXITCODE -eq 0) {
			Write-Host "Unity package branch is already up to date."
			return
		}

		$sourceRevision = if ([string]::IsNullOrWhiteSpace($env:GITHUB_SHA)) { "unknown" } else { $env:GITHUB_SHA }
		& git commit -m "chore(upm): publish Unity package from $sourceRevision"
		if ($LASTEXITCODE -ne 0) {
			throw "Failed to commit Unity package contents."
		}

		if ($branchExists) {
			& git push origin "HEAD:$Branch"
		}
		else {
			& git push --set-upstream origin $Branch
		}

		if ($LASTEXITCODE -ne 0) {
			throw "Failed to push Unity package branch '$Branch'."
		}
	}
	finally {
		Pop-Location
	}
}
finally {
	foreach ($entry in $previousGitConfig.GetEnumerator()) {
		[Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
	}
	$env:GIT_CONFIG_COUNT = $previousConfigCount
	if (Test-Path $publishRoot) {
		Remove-Item $publishRoot -Recurse -Force
	}
}

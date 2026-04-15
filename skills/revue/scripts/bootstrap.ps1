<#
.SYNOPSIS
    Bootstrap script for revue — downloads the platform-specific binary if needed.

.DESCRIPTION
    Detects the current platform, checks if the expected version of revue is
    cached locally, and downloads it from GitHub Releases if missing or outdated.
    Prints the path to the revue executable on success.

.PARAMETER SkillDir
    Path to the skill directory (containing VERSION file). Defaults to this script's directory parent.

.EXAMPLE
    $exe = & .\bootstrap.ps1
    & $exe /path/to/repo
#>
param(
    [string]$SkillDir
)

$ErrorActionPreference = 'Stop'

# ── Resolve paths ─────────────────────────────────────────────────────────────
if (-not $SkillDir) {
    $SkillDir = Split-Path -Parent $PSScriptRoot  # scripts/ → skill dir
}

$versionFile = Join-Path $SkillDir 'VERSION'
if (-not (Test-Path $versionFile)) {
    Write-Error "VERSION file not found at $versionFile"
    exit 1
}

$expectedVersion = (Get-Content $versionFile -Raw).Trim()
if (-not $expectedVersion) {
    Write-Error "VERSION file is empty"
    exit 1
}

# ── Detect platform ───────────────────────────────────────────────────────────
$os = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'win' }
      elseif ($IsMacOS) { 'osx' }
      elseif ($IsLinux) { 'linux' }
      else { Write-Error "Unsupported OS"; exit 1 }

$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLower()
$arch = switch ($arch) {
    'x64'   { 'x64' }
    'arm64' { 'arm64' }
    default { Write-Error "Unsupported architecture: $arch"; exit 1 }
}

$rid = "$os-$arch"
$exeName = if ($os -eq 'win') { 'revue.exe' } else { 'revue' }
$archiveExt = if ($os -eq 'win') { 'zip' } else { 'tar.gz' }

# ── Check for bundled binary (local dev install) ─────────────────────────────
$bundledExe = Join-Path $SkillDir $exeName
if (Test-Path $bundledExe) {
    try {
        $bundledVersion = (& $bundledExe --version 2>$null)
        if ($bundledVersion -and $bundledVersion.StartsWith($expectedVersion)) {
            Write-Output $bundledExe
            exit 0
        }
    } catch { }
}

# ── Cache directory ───────────────────────────────────────────────────────────
$cacheBase = if ($os -eq 'win') {
    Join-Path $env:LOCALAPPDATA 'revue'
} else {
    Join-Path $HOME '.cache' 'revue'
}

$cacheDir = Join-Path $cacheBase $expectedVersion
$cachedExe = Join-Path $cacheDir $exeName

# ── Check if already cached ───────────────────────────────────────────────────
if (Test-Path $cachedExe) {
    # Verify the cached binary reports the expected version
    try {
        $actualVersion = (& $cachedExe --version 2>$null)
        if ($actualVersion -and $actualVersion.StartsWith($expectedVersion)) {
            Write-Output $cachedExe
            exit 0
        }
        Write-Host "Cached binary version mismatch (expected $expectedVersion, got $actualVersion). Re-downloading..."
    } catch {
        Write-Host "Cached binary failed to run. Re-downloading..."
    }
}

# ── Download from GitHub Releases ─────────────────────────────────────────────
$owner = 'chsienki'
$repo = 'revue'
$tag = "v$expectedVersion"
$assetName = "revue-$rid.$archiveExt"
$downloadUrl = "https://github.com/$owner/$repo/releases/download/$tag/$assetName"

Write-Host "Downloading revue $expectedVersion for $rid..."
Write-Host "  $downloadUrl"

if (-not (Test-Path $cacheDir)) {
    New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
}

$tempFile = Join-Path ([System.IO.Path]::GetTempPath()) "revue-download-$([guid]::NewGuid().ToString('N')).$archiveExt"

try {
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $downloadUrl -OutFile $tempFile -UseBasicParsing

    # Extract
    if ($archiveExt -eq 'zip') {
        Expand-Archive -Path $tempFile -DestinationPath $cacheDir -Force
    } else {
        tar -xzf $tempFile -C $cacheDir
    }

    # Make executable on Unix
    if ($os -ne 'win') {
        chmod +x $cachedExe
    }
} catch {
    Write-Error "Failed to download revue: $_`nURL: $downloadUrl`nMake sure release $tag exists with asset $assetName"
    exit 1
} finally {
    if (Test-Path $tempFile) { Remove-Item $tempFile -Force }
}

# Verify it works
if (-not (Test-Path $cachedExe)) {
    Write-Error "Download succeeded but $exeName not found in extracted archive"
    exit 1
}

Write-Host "revue $expectedVersion installed to $cacheDir"

# ── Clean up old versions ─────────────────────────────────────────────────────
Get-ChildItem $cacheBase -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne $expectedVersion } |
    ForEach-Object {
        Write-Host "Removing old version: $($_.Name)"
        Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }

Write-Output $cachedExe

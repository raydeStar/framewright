<#
.SYNOPSIS
Writes a Framewright backup ZIP to disk.

.DESCRIPTION
`restore-backup.ps1` needs a ZIP produced by BackupService, but the only way to
obtain one was to click Download in the Settings drawer — so in practice there was
never a backup on disk to restore from, and the restore path had no input.

This asks the running local service for one, because BackupService uses SQLite's
online backup API and is therefore safe while the app is open. If no service is
reachable it falls back to copying a closed database directly, refusing to copy
one that still has a write-ahead log.

.EXAMPLE
scripts/create-backup.ps1
Backs up through the local service on its default port.

.EXAMPLE
scripts/create-backup.ps1 -DataRoot src/StoryboardStudio.Api/App_Data
Copies a closed development database when no service is running.

.NOTES
Runs under Windows PowerShell 5.1; this checkout has no pwsh on PATH.
#>
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) 'backups'),
    [string]$BaseUrl = 'http://127.0.0.1:5179',
    [string]$DataRoot
)

$ErrorActionPreference = 'Stop'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$resolvedDestination = [IO.Path]::GetFullPath($Destination)

function Test-ServiceReachable([string]$url) {
    try { $null = Invoke-RestMethod -Uri "$url/health" -TimeoutSec 4; return $true }
    catch { return $false }
}

if (Test-ServiceReachable $BaseUrl) {
    $target = Join-Path $resolvedDestination "framewright-backup-$timestamp.zip"
    Write-Host "Requesting a consistent backup from $BaseUrl ..."
    Invoke-WebRequest -Uri "$BaseUrl/api/maintenance/backup" -OutFile $target -TimeoutSec 600

    # A truncated download is worse than none, because it looks like a backup.
    # Verify the archive opens and carries the two entries restore-backup requires.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($target)
    try {
        $names = $archive.Entries | ForEach-Object { $_.FullName }
        foreach ($required in @('backup-manifest.json', 'database/storyboard-studio.db')) {
            if ($names -notcontains $required) { throw "The downloaded archive is missing $required." }
        }
        $assets = @($names | Where-Object { $_ -like 'assets/*' }).Count
    }
    finally { $archive.Dispose() }

    $size = [math]::Round((Get-Item -LiteralPath $target).Length / 1MB, 1)
    Write-Host "Wrote $target ($size MB, $assets asset entries)."
    Write-Host "Restore with: scripts/restore-backup.ps1 -BackupPath '$target' -DataRoot <data root>"
    return
}

if (-not $DataRoot) {
    throw @"
No Framewright service answered at $BaseUrl, and no -DataRoot was supplied.
Start the app and retry, or pass the data root to copy a closed database:
  pwsh scripts/create-backup.ps1 -DataRoot src/StoryboardStudio.Api/App_Data
"@
}

$resolvedDataRoot = [IO.Path]::GetFullPath($DataRoot)
$database = Join-Path $resolvedDataRoot 'storyboard-studio.db'
if (-not (Test-Path -LiteralPath $database -PathType Leaf)) { throw "No storyboard-studio.db under: $resolvedDataRoot" }
# Copying a database with a live write-ahead log captures a torn state: the
# committed data sits in the -wal file that this copy would leave behind.
foreach ($sidecar in 'storyboard-studio.db-wal', 'storyboard-studio.db-shm') {
    if (Test-Path -LiteralPath (Join-Path $resolvedDataRoot $sidecar) -PathType Leaf) {
        throw "$sidecar is present, so this database is still open. Close Framewright, or back up through the running service instead."
    }
}

$folder = Join-Path $resolvedDestination "App_Data-$timestamp"
Write-Host "No service reachable; copying the closed database from $resolvedDataRoot ..."
Copy-Item -LiteralPath $resolvedDataRoot -Destination $folder -Recurse
$copiedDatabase = Join-Path $folder ((Split-Path -Leaf $resolvedDataRoot) + '\storyboard-studio.db')
if (-not (Test-Path -LiteralPath $copiedDatabase -PathType Leaf)) {
    $copiedDatabase = Join-Path $folder 'storyboard-studio.db'
}
$source = (Get-FileHash -LiteralPath $database -Algorithm SHA256).Hash
$copy = (Get-FileHash -LiteralPath $copiedDatabase -Algorithm SHA256).Hash
if ($source -ne $copy) { throw "The copied database does not match its source. Treat $folder as unusable." }
$bytes = [math]::Round((Get-ChildItem -LiteralPath $folder -Recurse -File | Measure-Object -Sum Length).Sum / 1MB, 1)
Write-Host "Wrote $folder ($bytes MB). Database SHA256 verified: $source"
Write-Host 'This is a folder copy, not a ZIP, so restore it by copying it back over the data root.'

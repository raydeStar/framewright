[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Import-Module (Join-Path $repoRoot 'scripts\ReleaseCandidate.psm1') -Force

function Assert-Throws([scriptblock]$Action, [string]$Expected) {
    try { & $Action; throw "Expected failure containing '$Expected'." }
    catch {
        if ($_.Exception.Message -notmatch [Regex]::Escape($Expected)) { throw }
    }
}

Assert-Throws { Assert-ReleaseRepositoryState -DirtyPaths @('M README.md') -Ahead 0 -Behind 0 } 'clean repository'
Assert-Throws { Assert-ReleaseRepositoryState -DirtyPaths @() -Ahead 1 -Behind 0 } 'exactly match its upstream'
Assert-Throws { Assert-NoActiveFramewrightJobs -Jobs @([pscustomobject]@{ id = 'job-1'; state = 'Queued'; kind = 'Image' }) } 'active job'
Assert-NoActiveFramewrightJobs -Jobs @([pscustomobject]@{ id = 'job-2'; state = 'Completed'; kind = 'Image' })
Assert-Throws { Assert-NoActiveComfyUiJobs -Queue ([pscustomobject]@{ queue_running = @(@('prompt-1')); queue_pending = @() }) } 'ComfyUI is busy'
Assert-NoActiveComfyUiJobs -Queue ([pscustomobject]@{ queue_running = @(); queue_pending = @() })
Assert-Throws { Assert-ReleaseBuildMatch -ExpectedCommit 'abc' -HealthCommit 'old' -ImageCommit 'abc' } 'running Framewright build'
Assert-Throws { Assert-ReleaseBuildMatch -ExpectedCommit 'abc' -HealthCommit 'abc' -ImageCommit 'old' } 'image label'
Assert-ReleaseBuildMatch -ExpectedCommit 'abc' -HealthCommit 'abc' -ImageCommit 'abc'
if ((ConvertTo-ReleaseUtcTimestamp -Value '2026-08-31T09:11:34.420322-06:00') -ne '2026-08-31T15:11:34.4203220+00:00') {
    throw 'Release manifest build timestamps must be normalized to an explicit UTC offset.'
}
Assert-Throws { ConvertTo-ReleaseUtcTimestamp -Value 'not-a-timestamp' } 'invalid build timestamp'

$legacyRoot = Join-Path ([IO.Path]::GetTempPath()) ("framewright-legacy-backup-test-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $legacyRoot 'database'), (Join-Path $legacyRoot 'assets\aa') -Force | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.File]::WriteAllBytes((Join-Path $legacyRoot 'database\storyboard-studio.db'), [Text.Encoding]::ASCII.GetBytes("SQLite format 3`0legacy-test"))
    $assetBytes = [Text.Encoding]::UTF8.GetBytes('legacy content-addressed asset')
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $assetHash = ([BitConverter]::ToString($sha.ComputeHash($assetBytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $assetDirectory = Join-Path $legacyRoot ("assets\" + $assetHash.Substring(0, 2))
    New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $assetDirectory "$assetHash.png"), $assetBytes)
    $legacyManifest = [ordered]@{ schemaVersion = 1; product = 'Framewright'; includes = @('SQLite database', 'content-addressed assets') }
    [IO.File]::WriteAllText((Join-Path $legacyRoot 'backup-manifest.json'), ($legacyManifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $legacyZip = "$legacyRoot.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($legacyRoot, $legacyZip)
    $legacyResult = Test-FramewrightBackupArchive -Path $legacyZip
    if ($legacyResult.ManifestSchema -ne 1 -or $legacyResult.Entries -ne 2) { throw 'Legacy backup validation did not record the expected evidence.' }
}
finally {
    if (Test-Path -LiteralPath "$legacyRoot.zip") { Remove-Item -LiteralPath "$legacyRoot.zip" -Force }
    if (Test-Path -LiteralPath $legacyRoot) { Remove-Item -LiteralPath $legacyRoot -Recurse -Force }
}

$script = [IO.File]::ReadAllText((Join-Path $repoRoot 'scripts\prepare-release-candidate.ps1'))
$persistenceScript = [IO.File]::ReadAllText((Join-Path $repoRoot 'scripts\smoke-docker-persistence.ps1'))
$backupIndex = $script.IndexOf('/api/maintenance/backup', [StringComparison]::Ordinal)
$verifyIndex = $script.IndexOf("'verify.ps1'", [StringComparison]::Ordinal)
$rebuildIndex = $script.IndexOf("'docker.ps1') rebuild", [StringComparison]::Ordinal)
if ($backupIndex -lt 0 -or $verifyIndex -lt 0 -or $rebuildIndex -lt 0 -or $backupIndex -gt $verifyIndex -or $verifyIndex -gt $rebuildIndex) {
    throw 'Release ordering must remain backup, verification, then Framewright-only rebuild.'
}
if ($script -match '(?i)interrupt|queue\/clear|docker\s+(restart|stop).*comfy') {
    throw 'Release preparation contains a forbidden ComfyUI interruption or queue mutation.'
}
if ($script -match 'Cannot verify the ComfyUI queue before cutover') {
    throw 'Unavailable ComfyUI must be recorded as a QA blocker rather than preventing a safe Framewright-only candidate rebuild.'
}
if ($script -notmatch 'Assert-ReleaseBuildMatch') { throw 'Release preparation no longer proves commit/image identity.' }
if ($script -notmatch 'ConvertTo-ReleaseUtcTimestamp') { throw 'Release preparation no longer records build timestamps in UTC.' }
if ($script -match '@\(Invoke-RestMethod' -or $persistenceScript -match '@\(Invoke-RestMethod') {
    throw 'Invoke-RestMethod JSON arrays must be assigned before @() normalization; direct nesting produces one false collection item in PowerShell 7.'
}

Write-Host 'Release candidate contracts passed: dirty/unpushed work and active Framewright or ComfyUI jobs are refused, API collections stay flat, cutover ordering is safe, and build identity and UTC provenance must match.'

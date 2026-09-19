Set-StrictMode -Version Latest

function Assert-ReleaseRepositoryState {
    param(
        [string[]]$DirtyPaths,
        [int]$Ahead,
        [int]$Behind
    )
    if (@($DirtyPaths).Count -gt 0) {
        throw "The release candidate requires a clean repository. Commit or remove these changes first: $(@($DirtyPaths) -join ', ')"
    }
    if ($Ahead -ne 0 -or $Behind -ne 0) {
        throw "The release candidate commit must exactly match its upstream branch. Ahead=$Ahead Behind=$Behind."
    }
}

function Assert-NoActiveFramewrightJobs {
    param([object[]]$Jobs)
    $active = @($Jobs | Where-Object { $_.state -in @('Queued', 'Running') })
    if ($active.Count -gt 0) {
        $summary = $active | ForEach-Object { "$($_.id):$($_.state):$($_.kind)" }
        throw "Framewright has $($active.Count) active job(s). Wait for them before replacing the application: $($summary -join ', ')"
    }
}

function Assert-NoActiveComfyUiJobs {
    param([Parameter(Mandatory = $true)][object]$Queue)
    $running = @($Queue.queue_running).Count
    $pending = @($Queue.queue_pending).Count
    if ($running -gt 0 -or $pending -gt 0) {
        throw "ComfyUI is busy (running=$running pending=$pending). Candidate preparation is read-only toward ComfyUI and will not interrupt or replace this work."
    }
}

function Assert-ReleaseBuildMatch {
    param(
        [Parameter(Mandatory = $true)][string]$ExpectedCommit,
        [Parameter(Mandatory = $true)][string]$HealthCommit,
        [Parameter(Mandatory = $true)][string]$ImageCommit
    )
    if ($HealthCommit -ne $ExpectedCommit) {
        throw "The running Framewright build does not match Git HEAD. Expected=$ExpectedCommit Health=$HealthCommit."
    }
    if ($ImageCommit -ne $ExpectedCommit) {
        throw "The Framewright image label does not match Git HEAD. Expected=$ExpectedCommit Image=$ImageCommit."
    }
}

function ConvertTo-ReleaseUtcTimestamp {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Value)

    $timestamp = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
        $Value,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$timestamp)) {
        throw "Release provenance contains an invalid build timestamp: '$Value'."
    }

    return $timestamp.ToUniversalTime().ToString('O', [Globalization.CultureInfo]::InvariantCulture)
}

function Get-ReleaseSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-FramewrightBackupArchive {
    param([Parameter(Mandatory = $true)][string]$Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Backup archive is missing: $resolved" }
    $archive = [IO.Compression.ZipFile]::OpenRead($resolved)
    try {
        $manifestEntry = $archive.GetEntry('backup-manifest.json')
        if (-not $manifestEntry) { throw 'Backup archive does not contain backup-manifest.json.' }
        $reader = New-Object IO.StreamReader($manifestEntry.Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
        if ($manifest.product -ne 'Framewright' -or $manifest.schemaVersion -notin @(1, 2)) { throw 'Backup manifest version or product is invalid.' }
        if ($manifest.schemaVersion -eq 1) {
            $legacyEntries = @($archive.Entries | Where-Object { $_.FullName -ne 'backup-manifest.json' -and -not $_.FullName.Replace('\', '/').EndsWith('/') })
            $database = @($legacyEntries | Where-Object { $_.FullName.Replace('\', '/') -eq 'database/storyboard-studio.db' })
            if ($database.Count -ne 1 -or $database[0].Length -lt 16) { throw 'Legacy backup does not contain a plausible SQLite database.' }
            $databaseStream = $database[0].Open()
            try {
                $header = New-Object byte[] 16
                if ($databaseStream.Read($header, 0, $header.Length) -ne 16 -or [Text.Encoding]::ASCII.GetString($header) -ne "SQLite format 3`0") {
                    throw 'Legacy backup database header is invalid.'
                }
            }
            finally { $databaseStream.Dispose() }
            foreach ($entry in @($legacyEntries | Where-Object { $_.FullName.Replace('\', '/').StartsWith('assets/', [StringComparison]::Ordinal) })) {
                $entryPath = $entry.FullName.Replace('\', '/')
                if ($entryPath -notmatch '^assets/[0-9a-f]{2}/([0-9a-f]{64})\.[A-Za-z0-9]+$') { throw "Legacy backup asset path is not content-addressed: $entryPath" }
                $stream = $entry.Open()
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $actual = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
                finally { $sha.Dispose(); $stream.Dispose() }
                if ($actual -ne $Matches[1]) { throw "Legacy backup asset checksum failed: $entryPath" }
            }
            return [pscustomobject]@{ Entries = $legacyEntries.Count; Sha256 = Get-ReleaseSha256 -Path $resolved; ManifestSchema = 1 }
        }
        $listed = @($manifest.entries)
        if ($listed.Count -eq 0 -or 'database/storyboard-studio.db' -notin @($listed.path)) { throw 'Backup manifest does not inventory the database.' }
        $contentEntries = @($archive.Entries | Where-Object { $_.FullName -ne 'backup-manifest.json' -and -not $_.FullName.EndsWith('/') })
        if ($contentEntries.Count -ne $listed.Count) { throw 'Backup content count does not match its manifest.' }
        foreach ($expected in $listed) {
            $entry = $archive.GetEntry([string]$expected.path)
            if (-not $entry -or $entry.Length -ne [long]$expected.bytes) { throw "Backup entry is missing or changed: $($expected.path)" }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $actual = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
            finally { $sha.Dispose(); $stream.Dispose() }
            if ($actual -ne [string]$expected.sha256) { throw "Backup checksum failed: $($expected.path)" }
        }
        return [pscustomobject]@{ Entries = $listed.Count; Sha256 = Get-ReleaseSha256 -Path $resolved; ManifestSchema = 2 }
    }
    finally { $archive.Dispose() }
}

Export-ModuleMember -Function Assert-ReleaseRepositoryState, Assert-NoActiveFramewrightJobs, Assert-NoActiveComfyUiJobs, Assert-ReleaseBuildMatch, ConvertTo-ReleaseUtcTimestamp, Get-ReleaseSha256, Test-FramewrightBackupArchive

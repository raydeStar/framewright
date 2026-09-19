[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$restoreScript = Join-Path $repoRoot 'scripts\restore-backup.ps1'
$tempBase = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\script-tests'))
New-Item -ItemType Directory -Path $tempBase -Force | Out-Null
$testRoot = Join-Path $tempBase ("framewright-restore-test-{0}" -f [Guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $testRoot 'App_Data'
$archiveRoot = Join-Path $testRoot 'archive'
$backupPath = Join-Path $testRoot 'snapshot.zip'

function Write-TestDatabase([string]$Path, [string]$Marker) {
    $header = [Text.Encoding]::ASCII.GetBytes("SQLite format 3`0")
    $markerBytes = [Text.Encoding]::UTF8.GetBytes($Marker)
    $content = New-Object byte[] ($header.Length + $markerBytes.Length)
    [Array]::Copy($header, 0, $content, 0, $header.Length)
    [Array]::Copy($markerBytes, 0, $content, $header.Length, $markerBytes.Length)
    [IO.File]::WriteAllBytes($Path, $content)
}

function Get-TreeFingerprint([string]$Root) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return @(Get-ChildItem -LiteralPath $Root -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($prefix.Length).Replace([IO.Path]::DirectorySeparatorChar, '/')
        "$relative|$($_.Length)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    }) -join "`n"
}

New-Item -ItemType Directory -Path (Join-Path $dataRoot 'assets'), (Join-Path $dataRoot 'credentials'), (Join-Path $archiveRoot 'database'), (Join-Path $archiveRoot 'assets') | Out-Null
try {
    Write-TestDatabase (Join-Path $dataRoot 'storyboard-studio.db') 'current-generation'
    [IO.File]::WriteAllText((Join-Path $dataRoot 'assets\snapshot.bin'), 'stale snapshot bytes')
    [IO.File]::WriteAllText((Join-Path $dataRoot 'assets\new-only.bin'), 'must disappear')
    [IO.File]::WriteAllText((Join-Path $dataRoot 'credentials\openai.dpapi'), 'operational-secret-placeholder')

    $archiveDatabase = Join-Path $archiveRoot 'database\storyboard-studio.db'
    $archiveAsset = Join-Path $archiveRoot 'assets\snapshot.bin'
    Write-TestDatabase $archiveDatabase 'snapshot-generation'
    [IO.File]::WriteAllText($archiveAsset, 'snapshot bytes')
    $entries = @(
        [ordered]@{ path = 'database/storyboard-studio.db'; bytes = (Get-Item $archiveDatabase).Length; sha256 = (Get-FileHash $archiveDatabase -Algorithm SHA256).Hash.ToLowerInvariant() },
        [ordered]@{ path = 'assets/snapshot.bin'; bytes = (Get-Item $archiveAsset).Length; sha256 = (Get-FileHash $archiveAsset -Algorithm SHA256).Hash.ToLowerInvariant() }
    )
    $manifest = [ordered]@{ schemaVersion = 2; product = 'Framewright'; entries = $entries }
    [IO.File]::WriteAllText((Join-Path $archiveRoot 'backup-manifest.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Compress-Archive -Path (Join-Path $archiveRoot '*') -DestinationPath $backupPath

    & $restoreScript -BackupPath $backupPath -DataRoot $dataRoot -BaseUrl 'http://127.0.0.1:65431' -Confirm:$false
    if (Test-Path -LiteralPath (Join-Path $dataRoot 'assets\new-only.bin')) { throw 'Exact restore retained an asset created after the snapshot.' }
    if ([IO.File]::ReadAllText((Join-Path $dataRoot 'assets\snapshot.bin')) -ne 'snapshot bytes') { throw 'Snapshot asset was not restored.' }
    if (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'credentials\openai.dpapi') -PathType Leaf)) { throw 'Operational credential state was not preserved.' }

    # A fault after moving the original root must put that exact generation
    # back, without exposing the fully prepared replacement.
    Write-TestDatabase (Join-Path $dataRoot 'storyboard-studio.db') 'rollback-generation'
    [IO.File]::WriteAllText((Join-Path $dataRoot 'assets\rollback-only.bin'), 'survives failed restore')
    $beforeFailure = Get-TreeFingerprint $dataRoot
    $fakeBin = Join-Path $testRoot 'fake-bin'
    New-Item -ItemType Directory -Path $fakeBin | Out-Null
    # find.exe is a real native executable that rejects Docker's arguments.
    # Renaming it makes the stopped-daemon path deterministic in both Windows
    # PowerShell 5.1 and PowerShell 7 without relying on Add-Type executable
    # output, which the latter intentionally does not support.
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\find.exe') -Destination (Join-Path $fakeBin 'docker.exe')
    $previousFault = $env:FRAMEWRIGHT_RESTORE_TEST_FAIL_AFTER_OLD_MOVE
    $previousPath = $env:Path
    $env:FRAMEWRIGHT_RESTORE_TEST_FAIL_AFTER_OLD_MOVE = '1'
    $env:Path = $fakeBin
    $failedAsExpected = $false
    try {
        & $restoreScript -BackupPath $backupPath -DataRoot $dataRoot -BaseUrl 'http://127.0.0.1:65432' -Confirm:$false
    }
    catch {
        if ($_.Exception.Message -notmatch 'Injected restore failure') { throw }
        $failedAsExpected = $true
    }
    finally {
        $env:Path = $previousPath
        if ($null -eq $previousFault) { Remove-Item Env:FRAMEWRIGHT_RESTORE_TEST_FAIL_AFTER_OLD_MOVE -ErrorAction SilentlyContinue }
        else { $env:FRAMEWRIGHT_RESTORE_TEST_FAIL_AFTER_OLD_MOVE = $previousFault }
    }
    if (-not $failedAsExpected) { throw 'The restore fault injection did not execute.' }
    if ((Get-TreeFingerprint $dataRoot) -ne $beforeFailure) { throw 'A failed restore did not roll the original generation back exactly.' }
    $stages = @(Get-ChildItem -LiteralPath $testRoot -Directory | Where-Object Name -Like 'App_Data.restore-stage-*')
    if ($stages.Count -ne 0) { throw 'A failed restore leaked a staging directory.' }
    Write-Host 'Restore snapshot tests passed: exact inventory replacement and atomic rollback are verified.'
}
finally {
    $resolvedTest = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTest.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTest).StartsWith('framewright-restore-test-', [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTest)) {
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force
    }
}

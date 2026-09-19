[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupPath,
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'Framewright\App_Data'),
    [uri]$BaseUrl = 'http://127.0.0.1:5179'
)

$ErrorActionPreference = 'Stop'
$resolvedBackup = [IO.Path]::GetFullPath($BackupPath)
$resolvedData = [IO.Path]::GetFullPath($DataRoot)
$localAppData = [IO.Path]::GetFullPath($env:LOCALAPPDATA)
$localAppDataPrefix = $localAppData.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$tempRoot = Join-Path $tempBase ("storyboard-studio-restore-{0}" -f [Guid]::NewGuid().ToString('N'))

if (-not (Test-Path -LiteralPath $resolvedBackup -PathType Leaf) -or [IO.Path]::GetExtension($resolvedBackup) -ne '.zip') {
    throw "BackupPath must identify a Framewright ZIP backup: $resolvedBackup"
}

# The installed app keeps its data under LocalAppData, but a development checkout
# keeps it in the repository at src\StoryboardStudio.Api\App_Data — and the
# original LocalAppData-only guard meant the database actually being worked on
# could not be restored at all. Rather than widening this to any path, a target
# outside LocalAppData is accepted only when it already *is* a Framewright data
# root: it must contain storyboard-studio.db. That keeps the useful case working
# while still refusing an arbitrary or mistyped directory.
$isInsideLocalAppData = $resolvedData -ne $localAppData -and $resolvedData.StartsWith($localAppDataPrefix, [StringComparison]::OrdinalIgnoreCase)
$looksLikeExistingDataRoot = Test-Path -LiteralPath (Join-Path $resolvedData 'storyboard-studio.db') -PathType Leaf
if (-not $isInsideLocalAppData -and -not $looksLikeExistingDataRoot) {
    throw @"
DataRoot must be either a dedicated folder inside LocalAppData, or an existing
Framewright data root containing storyboard-studio.db.
Refusing: $resolvedData
"@
}
# A drive root or a Windows directory can satisfy neither test above by accident,
# but refuse them explicitly so a stray database file could never authorise one.
$root = [IO.Path]::GetPathRoot($resolvedData).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ($resolvedData.TrimEnd([IO.Path]::DirectorySeparatorChar) -eq $root) { throw "DataRoot cannot be a drive root: $resolvedData" }
foreach ($protected in @($env:WINDIR, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:USERPROFILE)) {
    if ([string]::IsNullOrWhiteSpace($protected)) { continue }
    if ($resolvedData.TrimEnd([IO.Path]::DirectorySeparatorChar) -eq [IO.Path]::GetFullPath($protected).TrimEnd([IO.Path]::DirectorySeparatorChar)) {
        throw "DataRoot cannot be a system or profile root: $resolvedData"
    }
}
if ($isInsideLocalAppData -and (Get-Process -Name 'Framewright' -ErrorAction SilentlyContinue)) {
    throw 'Close Framewright before restoring a backup.'
}
# Refuse a native development server even though its process is named dotnet.
# Command-line inspection is best-effort because locked-down Windows accounts
# may not grant Win32_Process details; the HTTP and SQLite checks still apply.
$liveDotnet = $null
if ($isInsideLocalAppData) {
    try {
        $liveDotnet = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction Stop |
            Where-Object { $_.CommandLine -match 'StoryboardStudio\.Api' } |
            Select-Object -First 1
    }
    catch {
        Write-Verbose "Could not inspect dotnet command lines: $($_.Exception.Message)"
    }
    if ($liveDotnet) { throw 'Close the running Framewright development server before restoring a backup.' }
}

# A bound HTTP port is the most reliable cross-host signal: it detects a
# packaged executable, dotnet run, or a container regardless of process name.
# Refuse any listener on that configured port; overwriting the database is not
# the occasion to guess whether a different-looking response is harmless.
$portIsOpen = $false
$tcpClient = [Net.Sockets.TcpClient]::new()
try {
    $connect = $tcpClient.ConnectAsync($BaseUrl.DnsSafeHost, $BaseUrl.Port)
    if ($connect.Wait(2000) -and $tcpClient.Connected) { $portIsOpen = $true }
}
catch {
    # Connection refusal means no server owns this configured address.
}
finally {
    $tcpClient.Dispose()
}
if ($portIsOpen) { throw "A service is listening at $BaseUrl. Stop Framewright before restoring a backup." }

# A container can be live with a changed/unpublished port, so inspect its state
# independently of the HTTP probe. This is read-only and harmless when Docker is
# not installed or Desktop is stopped.
$docker = Get-Command docker -ErrorAction SilentlyContinue
if ($docker) {
    function Invoke-DockerInspect([string]$Format) {
        # Windows PowerShell 5.1 promotes native stderr to a terminating error
        # under Stop, before LASTEXITCODE can be inspected. Use Process directly
        # so a stopped/inaccessible Docker Desktop remains a harmless absence.
        $process = $null
        try {
            $start = New-Object Diagnostics.ProcessStartInfo
            $start.FileName = $docker.Source
            $start.Arguments = "inspect --format `"$Format`" framewright"
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $process = [Diagnostics.Process]::Start($start)
            $output = $process.StandardOutput.ReadToEnd()
            $errorOutput = $process.StandardError.ReadToEnd()
            $process.WaitForExit()
            return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output; Error = $errorOutput }
        }
        catch {
            return [pscustomobject]@{ ExitCode = -1; Output = ''; Error = $_.Exception.Message }
        }
        finally {
            if ($process) { $process.Dispose() }
        }
    }

    $stateProbe = Invoke-DockerInspect '{{.State.Running}}'
    if ($stateProbe.ExitCode -eq 0 -and $stateProbe.Output.Trim() -eq 'true') {
        $mountProbe = Invoke-DockerInspect '{{json .Mounts}}'
        if ($mountProbe.ExitCode -ne 0) {
            throw 'The Framewright Docker container is running and its data mount could not be verified. Stop it before restoring a backup.'
        }
        $mountJson = $mountProbe.Output
        $mountedTarget = $null
        try {
            # Windows PowerShell 5.1 can keep a JSON array as one pipeline
            # object. Materialize it first so /data is inspected reliably.
            $mounts = ConvertFrom-Json -InputObject ($mountJson -join '')
            $dataMount = @($mounts) | Where-Object { $_.Destination -eq '/data' } | Select-Object -First 1
            if ($dataMount -and -not [string]::IsNullOrWhiteSpace([string]$dataMount.Source)) {
                $mountedTarget = [IO.Path]::GetFullPath([string]$dataMount.Source)
            }
        }
        catch {
            throw 'The Framewright Docker container is running and its data mount could not be verified. Stop it before restoring a backup.'
        }
        if ($null -eq $mountedTarget -or $mountedTarget.TrimEnd([IO.Path]::DirectorySeparatorChar).Equals($resolvedData.TrimEnd([IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The Framewright Docker container is using this data root. Stop it before restoring a backup.'
        }
    }
}

# Finally acquire an exclusive handle on the actual database. This catches a
# live SQLite owner even when it uses a custom port or process name. Any failure
# is treated as unsafe; a restore must prove exclusivity, not merely hope for it.
$targetDatabase = Join-Path $resolvedData 'storyboard-studio.db'
if (Test-Path -LiteralPath $targetDatabase -PathType Leaf) {
    try {
        $exclusiveDatabase = [IO.File]::Open($targetDatabase, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $exclusiveDatabase.Dispose()
    }
    catch {
        throw "Framewright's SQLite database is open or cannot be locked exclusively. Stop every app instance before restoring: $resolvedData"
    }
}
# A dev checkout runs the API under dotnet rather than as Framewright.exe, so the
# process check above cannot see it. Restoring underneath a live server would let
# it keep writing to the file that was just replaced.
if (Test-Path -LiteralPath (Join-Path $resolvedData 'storyboard-studio.db-wal') -PathType Leaf) {
    throw "A write-ahead log is present, which means a Framewright process still has this database open: $resolvedData"
}

# Internal validation scratch space is not the destructive restore target. It
# must still be created and removed during -WhatIf so a rehearsal validates the
# real archive without leaking temporary directories.
New-Item -ItemType Directory -Path $tempRoot -WhatIf:$false | Out-Null
try {
    Expand-Archive -LiteralPath $resolvedBackup -DestinationPath $tempRoot
    $manifest = Join-Path $tempRoot 'backup-manifest.json'
    $database = Join-Path $tempRoot 'database\storyboard-studio.db'
    if (-not (Test-Path -LiteralPath $manifest) -or -not (Test-Path -LiteralPath $database)) {
        throw 'The archive is not a valid Framewright backup.'
    }
    $metadata = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $schemaVersion = [int]$metadata.schemaVersion
    if ($schemaVersion -notin @(1, 2)) { throw "Unsupported backup schema version: $schemaVersion" }
    if ($metadata.product -ne 'Framewright') { throw "The backup product was not recognized: $($metadata.product)" }

    # Schema 2 inventories every payload file. Verify the inventory before
    # ShouldProcess is reached so -WhatIf remains a useful, read-only restore
    # rehearsal rather than a leap of faith in a merely well-shaped ZIP.
    if ($schemaVersion -eq 2) {
        $entries = @($metadata.entries)
        if ($entries.Count -eq 0) { throw 'The backup manifest does not contain a file inventory.' }

        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $tempPrefix = [IO.Path]::GetFullPath($tempRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        foreach ($entry in $entries) {
            $archivePath = [string]$entry.path
            $segments = @($archivePath -split '/')
            $isAllowedPath = $archivePath -eq 'database/storyboard-studio.db' -or $archivePath.StartsWith('assets/', [StringComparison]::Ordinal)
            $isUnsafePath = [string]::IsNullOrWhiteSpace($archivePath) -or
                $archivePath.StartsWith('/', [StringComparison]::Ordinal) -or
                $archivePath.Contains('\') -or
                $segments.Count -eq 0 -or
                @($segments | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -in @('.', '..') }).Count -gt 0
            if (-not $isAllowedPath -or $isUnsafePath) { throw "The backup manifest contains an unsafe path: $archivePath" }
            if (-not $seen.Add($archivePath)) { throw "The backup manifest contains a duplicate path: $archivePath" }

            $relativePath = $archivePath.Replace('/', [IO.Path]::DirectorySeparatorChar)
            $payloadPath = [IO.Path]::GetFullPath((Join-Path $tempRoot $relativePath))
            if (-not $payloadPath.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
                throw "The backup manifest references a missing file: $archivePath"
            }

            try { $expectedBytes = [Convert]::ToInt64($entry.bytes) }
            catch { throw "The backup manifest contains an invalid byte length: $archivePath" }
            $expectedHash = [string]$entry.sha256
            $actualLength = (Get-Item -LiteralPath $payloadPath).Length
            if ($expectedBytes -lt 0 -or $actualLength -ne $expectedBytes) { throw "The backup entry length changed: $archivePath" }
            if ($expectedHash -notmatch '^[0-9a-fA-F]{64}$') { throw "The backup manifest contains an invalid checksum: $archivePath" }
            $actualHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash
            if (-not $actualHash.Equals($expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
                throw "The backup entry checksum failed: $archivePath"
            }
        }

        if (-not $seen.Contains('database/storyboard-studio.db')) { throw 'The backup inventory does not include its SQLite database.' }
        $payloadFiles = @(Get-ChildItem -LiteralPath $tempRoot -File -Recurse | Where-Object { $_.FullName -ne $manifest })
        if ($payloadFiles.Count -ne $seen.Count) { throw 'The backup content does not match its manifest inventory.' }
        foreach ($payloadFile in $payloadFiles) {
            $archivePath = $payloadFile.FullName.Substring($tempPrefix.Length).Replace([IO.Path]::DirectorySeparatorChar, '/')
            if (-not $seen.Contains($archivePath)) { throw "The backup contains an unlisted file: $archivePath" }
        }
    }
    $header = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($database), 0, 16)
    if ($header -ne "SQLite format 3`0") { throw 'The backup database does not have a valid SQLite header.' }

    if (-not $PSCmdlet.ShouldProcess($resolvedData, "Restore $resolvedBackup")) { return }
    # Build a complete replacement beside the live data root before touching
    # the current generation. Copy only operational state that backups
    # intentionally exclude (credentials, retained backups, readiness state),
    # then add the archive's exact database and asset inventory. In particular,
    # never merge assets into the existing directory: a snapshot restore must
    # remove content created after that snapshot instead of leaving orphans.
    $dataParent = [IO.Path]::GetFullPath((Split-Path -Parent $resolvedData))
    $dataLeaf = Split-Path -Leaf $resolvedData
    $nonce = [Guid]::NewGuid().ToString('N')
    $stageRoot = [IO.Path]::GetFullPath((Join-Path $dataParent "$dataLeaf.restore-stage-$nonce"))
    $safetyCopy = [IO.Path]::GetFullPath((Join-Path $dataParent "$dataLeaf.pre-restore-$(Get-Date -Format 'yyyyMMdd-HHmmss')-$nonce"))
    foreach ($swapPath in @($stageRoot, $safetyCopy)) {
        if ((Split-Path -Parent $swapPath) -ne $dataParent -or
            -not (Split-Path -Leaf $swapPath).StartsWith($dataLeaf + '.', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Restore staging escaped the intended data parent: $swapPath"
        }
        if (Test-Path -LiteralPath $swapPath) { throw "Restore staging path already exists: $swapPath" }
    }

    $oldRootMoved = $false
    try {
        if (-not (Test-Path -LiteralPath $dataParent -PathType Container)) {
            New-Item -ItemType Directory -Path $dataParent -Force | Out-Null
        }
        New-Item -ItemType Directory -Path $stageRoot | Out-Null
        if (Test-Path -LiteralPath $resolvedData -PathType Container) {
            foreach ($item in Get-ChildItem -LiteralPath $resolvedData -Force) {
                if ($item.Name -in @('storyboard-studio.db', 'storyboard-studio.db-wal', 'storyboard-studio.db-shm', 'assets')) { continue }
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "The data root contains an unsupported link and cannot be restored safely: $($item.FullName)"
                }
                Copy-Item -LiteralPath $item.FullName -Destination $stageRoot -Recurse -Force
            }
        }

        Copy-Item -LiteralPath $database -Destination (Join-Path $stageRoot 'storyboard-studio.db')
        $stagedAssets = Join-Path $stageRoot 'assets'
        New-Item -ItemType Directory -Path $stagedAssets | Out-Null
        $assets = Join-Path $tempRoot 'assets'
        if (Test-Path -LiteralPath $assets -PathType Container) {
            foreach ($item in Get-ChildItem -LiteralPath $assets -Force) {
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "The backup contains an unsupported asset link: $($item.FullName)"
                }
                Copy-Item -LiteralPath $item.FullName -Destination $stagedAssets -Recurse
            }
        }

        # Validate the staged payload before the same-volume directory swap.
        $stagedDatabase = Join-Path $stageRoot 'storyboard-studio.db'
        $stagedHeader = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($stagedDatabase), 0, 16)
        if ($stagedHeader -ne "SQLite format 3`0") { throw 'The staged backup database failed validation.' }
        if ($schemaVersion -eq 2) {
            $expectedAssets = @($entries | Where-Object { ([string]$_.path).StartsWith('assets/', [StringComparison]::Ordinal) }).Count
            $actualAssets = @(Get-ChildItem -LiteralPath $stagedAssets -File -Recurse).Count
            if ($actualAssets -ne $expectedAssets) { throw 'The staged asset inventory does not exactly match the backup.' }
        }

        # Renaming directories on the same volume is atomic. If the second
        # rename fails (or the test-only fault is injected), restore the old
        # generation immediately rather than exposing a database/assets mix.
        if (Test-Path -LiteralPath $resolvedData -PathType Container) {
            Move-Item -LiteralPath $resolvedData -Destination $safetyCopy
            $oldRootMoved = $true
            if ($env:FRAMEWRIGHT_RESTORE_TEST_FAIL_AFTER_OLD_MOVE -eq '1') {
                throw 'Injected restore failure after moving the original data root.'
            }
        }
        Move-Item -LiteralPath $stageRoot -Destination $resolvedData
        $stageRoot = $null
        if ($oldRootMoved) { Write-Host "Created recoverable safety copy: $safetyCopy" }
        Write-Host "Restored exact Framewright snapshot to $resolvedData"
    }
    catch {
        if ($oldRootMoved -and -not (Test-Path -LiteralPath $resolvedData) -and (Test-Path -LiteralPath $safetyCopy -PathType Container)) {
            Move-Item -LiteralPath $safetyCopy -Destination $resolvedData
            $oldRootMoved = $false
        }
        throw
    }
    finally {
        if ($stageRoot -and (Test-Path -LiteralPath $stageRoot -PathType Container)) {
            Remove-Item -LiteralPath $stageRoot -Recurse -Force
        }
    }
}
finally {
    $resolvedTemp = [IO.Path]::GetFullPath($tempRoot)
    if ($resolvedTemp.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemp).StartsWith('storyboard-studio-restore-', [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTemp)) {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force -WhatIf:$false
    }
}

[CmdletBinding()]
param(
    [string]$PublishPath,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Framewright'),
    [string]$VoiceRuntimeRoot = (Join-Path $env:LOCALAPPDATA 'FramewrightVoice'),
    [ValidateSet('', 'AfterActivation')]
    [string]$TestFailurePoint = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($PublishPath)) { $PublishPath = Join-Path $repoRoot 'artifacts\framewright-win-x64' }
$resolvedPublish = [IO.Path]::GetFullPath($PublishPath)
$resolvedInstall = [IO.Path]::GetFullPath($InstallRoot)
$resolvedVoice = [IO.Path]::GetFullPath($VoiceRuntimeRoot)
$localAppData = [IO.Path]::GetFullPath($env:LOCALAPPDATA)
$localAppDataPrefix = $localAppData.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

foreach ($candidate in @($resolvedInstall, $resolvedVoice)) {
    if ($candidate -eq $localAppData -or -not $candidate.StartsWith($localAppDataPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Install and voice runtime roots must be dedicated folders inside LocalAppData: $candidate"
    }
}
foreach ($required in @('Framewright.exe', 'scripts\start-installed.ps1', 'scripts\start-voice-worker.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedPublish $required) -PathType Leaf)) {
        throw "Publish Framewright first. Required package file not found: $required"
    }
}
if (Test-Path -LiteralPath (Join-Path $resolvedPublish 'App_Data')) {
    throw 'The package contains App_Data. Refusing to overwrite installed artist data.'
}
$installedExecutable = [IO.Path]::GetFullPath((Join-Path $resolvedInstall 'Framewright.exe'))
$runningInstalledApp = @(Get-Process -Name 'Framewright' -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and ([IO.Path]::GetFullPath($_.Path)).Equals($installedExecutable, [StringComparison]::OrdinalIgnoreCase) }
    catch { $false }
})
if ($runningInstalledApp.Count -gt 0) {
    throw "Close the Framewright instance running from $resolvedInstall before installing or updating it."
}

function Migrate-LegacyVoiceState {
    if (-not (Test-Path -LiteralPath $resolvedInstall -PathType Container)) { return }
    $stateRoot = Join-Path $resolvedVoice 'state'
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    $tokenPath = Join-Path $stateRoot 'voice-worker.token'
    $legacyEnv = Join-Path $resolvedInstall '.env'
    if (-not (Test-Path -LiteralPath $tokenPath -PathType Leaf) -and (Test-Path -LiteralPath $legacyEnv -PathType Leaf)) {
        $line = [IO.File]::ReadAllLines($legacyEnv) | Where-Object { $_ -match '^FRAMEWRIGHT_VOICE_WORKER_TOKEN=' } | Select-Object -First 1
        if ($line) { [IO.File]::WriteAllText($tokenPath, $line.Substring($line.IndexOf('=') + 1).Trim(), [Text.UTF8Encoding]::new($false)) }
    }
    $legacyState = Join-Path $resolvedInstall '.tmp'
    foreach ($name in @('voice-worker.pid', 'voice-worker.stdout.log', 'voice-worker.stderr.log')) {
        $source = Join-Path $legacyState $name
        $target = Join-Path $stateRoot $name
        if (-not (Test-Path -LiteralPath $target) -and (Test-Path -LiteralPath $source -PathType Leaf)) {
            Copy-Item -LiteralPath $source -Destination $target
        }
    }
}

function Stop-ProvenVoiceWorker {
    $pidPath = Join-Path $resolvedVoice 'state\voice-worker.pid'
    if (-not (Test-Path -LiteralPath $pidPath -PathType Leaf)) { return }
    try { $state = Get-Content -LiteralPath $pidPath -Raw | ConvertFrom-Json }
    catch { throw "Voice worker state is unreadable at $pidPath. Refusing an update that could orphan a GPU worker." }
    $candidate = Get-Process -Id ([int]$state.pid) -ErrorAction SilentlyContinue
    if (-not $candidate) { Remove-Item -LiteralPath $pidPath -Force; return }
    try {
        $expectedStart = [DateTimeOffset]::Parse([string]$state.processStartTimeUtc, [Globalization.CultureInfo]::InvariantCulture)
        $actualStart = [DateTimeOffset]$candidate.StartTime.ToUniversalTime()
        $python = [IO.Path]::GetFullPath([string]$state.python)
        $details = Get-CimInstance Win32_Process -Filter "ProcessId = $($candidate.Id)" -ErrorAction Stop
        $commandLine = [string]$details.CommandLine
        $owned = [Math]::Abs(($actualStart - $expectedStart).TotalSeconds) -le 2 -and
            ([IO.Path]::GetFullPath($candidate.Path)).Equals($python, [StringComparison]::OrdinalIgnoreCase) -and
            -not [string]::IsNullOrWhiteSpace($commandLine) -and
            $commandLine.IndexOf([string]$state.workerScript, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $commandLine.IndexOf([string]$state.manifest, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $commandLine.IndexOf([string]$state.modelLock, [StringComparison]::OrdinalIgnoreCase) -ge 0
    }
    catch { $owned = $false }
    if (-not $owned) {
        throw "Process $($candidate.Id) is recorded as the voice worker but failed the exact ownership proof. It will not be killed; reboot or stop it explicitly before updating."
    }
    Stop-Process -Id $candidate.Id -Force
    $candidate.WaitForExit(5000)
    Remove-Item -LiteralPath $pidPath -Force
}

Migrate-LegacyVoiceState
Stop-ProvenVoiceWorker

$installParent = Split-Path -Parent $resolvedInstall
$installLeaf = Split-Path -Leaf $resolvedInstall
New-Item -ItemType Directory -Path $installParent -Force | Out-Null
$nonce = [Guid]::NewGuid().ToString('N')
$stage = [IO.Path]::GetFullPath((Join-Path $installParent "$installLeaf.installing-$nonce"))
$rollback = [IO.Path]::GetFullPath((Join-Path $installParent "$installLeaf.previous-$nonce"))
$failed = [IO.Path]::GetFullPath((Join-Path $installParent "$installLeaf.failed-$nonce"))
$parentPrefix = [IO.Path]::GetFullPath($installParent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($candidate in @($stage, $rollback, $failed)) {
    if (-not $candidate.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Split-Path -Leaf $candidate).StartsWith("$installLeaf.", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe installer transaction path: $candidate"
    }
}

$oldMoved = $false
$activated = $false
$dataMoved = $false
$success = $false
try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    Get-ChildItem -LiteralPath $resolvedPublish -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stage -Recurse -Force
    }

    $publishPrefix = $resolvedPublish.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $stagePrefix = $stage.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $published = @{}
    Get-ChildItem -LiteralPath $resolvedPublish -Recurse -File | ForEach-Object {
        $relative = ([IO.Path]::GetFullPath($_.FullName)).Substring($publishPrefix.Length)
        $published[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    $stagedFiles = @(Get-ChildItem -LiteralPath $stage -Recurse -File)
    if ($stagedFiles.Count -ne $published.Count) { throw 'The staged install does not contain the exact published file inventory.' }
    foreach ($file in $stagedFiles) {
        $relative = ([IO.Path]::GetFullPath($file.FullName)).Substring($stagePrefix.Length)
        if (-not $published.ContainsKey($relative) -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $published[$relative]) {
            throw "The staged package failed integrity validation: $relative"
        }
    }

    if (Test-Path -LiteralPath $resolvedInstall) {
        Move-Item -LiteralPath $resolvedInstall -Destination $rollback
        $oldMoved = $true
    }
    Move-Item -LiteralPath $stage -Destination $resolvedInstall
    $activated = $true
    if ($TestFailurePoint -eq 'AfterActivation') { throw 'Injected installer failure after activation.' }

    $oldData = Join-Path $rollback 'App_Data'
    $newData = Join-Path $resolvedInstall 'App_Data'
    if ($oldMoved -and (Test-Path -LiteralPath $oldData)) {
        if (Test-Path -LiteralPath $newData) { throw 'The staged package unexpectedly created App_Data.' }
        Move-Item -LiteralPath $oldData -Destination $newData
        $dataMoved = $true
    }

    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
    $shortcutPath = Join-Path $startMenu 'Framewright.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $shortcut.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$(Join-Path $resolvedInstall 'scripts\start-installed.ps1')`""
    $shortcut.WorkingDirectory = $resolvedInstall
    $shortcut.IconLocation = "$(Join-Path $resolvedInstall 'Framewright.exe'),0"
    $shortcut.Description = 'Framewright — build every shot with intention'
    $shortcut.Save()
    $success = $true

    Write-Host "Installed Framewright atomically at $resolvedInstall"
    Write-Host "Start menu shortcut: $shortcutPath"
    Write-Host 'Artist data and durable voice state survive in-place updates.'
}
catch {
    $failure = $_
    if ($activated) {
        if ($dataMoved -and (Test-Path -LiteralPath (Join-Path $resolvedInstall 'App_Data'))) {
            Move-Item -LiteralPath (Join-Path $resolvedInstall 'App_Data') -Destination (Join-Path $rollback 'App_Data')
            $dataMoved = $false
        }
        if (Test-Path -LiteralPath $resolvedInstall) { Move-Item -LiteralPath $resolvedInstall -Destination $failed }
        if ($oldMoved -and (Test-Path -LiteralPath $rollback)) { Move-Item -LiteralPath $rollback -Destination $resolvedInstall }
    } elseif ($oldMoved -and -not (Test-Path -LiteralPath $resolvedInstall) -and (Test-Path -LiteralPath $rollback)) {
        Move-Item -LiteralPath $rollback -Destination $resolvedInstall
    }
    throw $failure
}
finally {
    foreach ($candidate in @($stage, $failed)) {
        if ($candidate.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $candidate)) {
            Remove-Item -LiteralPath $candidate -Recurse -Force
        }
    }
    if ($success -and $oldMoved -and $rollback.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $rollback)) {
        Remove-Item -LiteralPath $rollback -Recurse -Force
    }
}

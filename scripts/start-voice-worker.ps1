param(
    [switch]$Restart,
    [string]$VoiceRuntimeRoot,
    [string]$VoicePythonPath,
    [ValidateRange(1, 65535)]
    [int]$WorkerPort = 5181,
    [ValidateRange(5, 600)]
    [int]$StartupTimeoutSeconds = 120,
    [switch]$SkipUserEnvironmentUpdate
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
# A long-lived Codex or terminal process can retain a stale PATH after the
# workstation voice bootstrap installs SoX. Refresh the user segment before
# launching the worker so Qwen's audio helpers can always resolve it.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not [string]::IsNullOrWhiteSpace($userPath)) {
    $env:Path = "$userPath;$env:Path"
}
$runtimePython = 'C:\Comfy\python_embeded\python.exe'
$configuredRuntime = [Environment]::GetEnvironmentVariable('FRAMEWRIGHT_VOICE_RUNTIME_ROOT', 'User')
$runtimeRoot = if (-not [string]::IsNullOrWhiteSpace($VoiceRuntimeRoot)) {
    $VoiceRuntimeRoot
} elseif ([string]::IsNullOrWhiteSpace($configuredRuntime)) {
    # LOCALAPPDATA is virtualized when this launcher is invoked from a
    # packaged desktop host. Keep the shared voice runtime in the user's real
    # Windows profile so Framewright, Codex, and the installed app see one lock.
    Join-Path (Join-Path $env:USERPROFILE 'AppData\Local') 'FramewrightVoice'
} else {
    $configuredRuntime
}
$configuredPython = [Environment]::GetEnvironmentVariable('QwenTts__PythonPath', 'User')
if (-not [string]::IsNullOrWhiteSpace($VoicePythonPath)) {
    $runtimePython = $VoicePythonPath
} elseif (-not [string]::IsNullOrWhiteSpace($configuredPython)) {
    $runtimePython = $configuredPython
}
$runtimePackages = Join-Path $runtimeRoot 'packages'
$runtimeManifest = Join-Path $runtimeRoot 'runtime-manifest.json'
$workerScript = Join-Path $repoRoot 'tools\voice\qwen_voice_worker.py'
$modelLock = Join-Path $repoRoot 'tools\voice\models.lock.json'
$stateRoot = Join-Path $runtimeRoot 'state'
$pidPath = Join-Path $stateRoot 'voice-worker.pid'
$stdoutPath = Join-Path $stateRoot 'voice-worker.stdout.log'
$stderrPath = Join-Path $stateRoot 'voice-worker.stderr.log'
$tokenPath = Join-Path $stateRoot 'voice-worker.token'
$legacyPidPath = Join-Path $repoRoot '.tmp\voice-worker.pid'
$legacyEnvPath = Join-Path $repoRoot '.env'
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $pidPath -PathType Leaf) -and (Test-Path -LiteralPath $legacyPidPath -PathType Leaf)) {
    Copy-Item -LiteralPath $legacyPidPath -Destination $pidPath
}

$token = if (Test-Path -LiteralPath $tokenPath -PathType Leaf) {
    ([System.IO.File]::ReadAllText($tokenPath)).Trim()
} else {
    $legacyLines = if (Test-Path -LiteralPath $legacyEnvPath -PathType Leaf) { [System.IO.File]::ReadAllLines($legacyEnvPath) } else { @() }
    $legacyToken = $legacyLines | Where-Object { $_ -match '^FRAMEWRIGHT_VOICE_WORKER_TOKEN=' } | Select-Object -First 1
    if ($legacyToken) { $legacyToken.Substring($legacyToken.IndexOf('=') + 1).Trim() } else { '' }
}
if ($token.Length -lt 32 -or $token -eq 'replace-with-a-long-random-token') {
    $bytes = New-Object byte[] 32
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    $token = ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
    Write-Host 'Created a workstation-only voice worker token in the durable local voice runtime.'
}
[System.IO.File]::WriteAllText($tokenPath, $token, [System.Text.UTF8Encoding]::new($false))
if (-not $SkipUserEnvironmentUpdate) {
    $env:QwenTts__WorkerEndpoint = "http://127.0.0.1:$WorkerPort"
    $env:QwenTts__WorkerToken = $token
    [Environment]::SetEnvironmentVariable('QwenTts__WorkerEndpoint', "http://127.0.0.1:$WorkerPort", 'User')
    [Environment]::SetEnvironmentVariable('QwenTts__WorkerToken', $token, 'User')
}

foreach ($requiredPath in @($runtimePython, $workerScript, $modelLock)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required local voice path is missing: $requiredPath. Run .\scripts\setup-voice-worker.ps1 to prepare the workstation."
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $runtimePackages 'qwen_tts') -PathType Container)) {
    throw "The pinned local voice packages are not installed at $runtimePackages. Run .\scripts\setup-voice-worker.ps1 once."
}
if (-not (Test-Path -LiteralPath $runtimeManifest -PathType Leaf)) {
    throw "The verified voice runtime manifest is missing at $runtimeManifest. Run .\scripts\setup-voice-worker.ps1 once."
}

function Test-WorkerHealth {
    param([string]$WorkerToken)

    try {
        $headers = @{ 'X-Framewright-Voice-Token' = $WorkerToken }
        $response = Invoke-RestMethod -Uri "http://127.0.0.1:$WorkerPort/health" -Headers $headers -TimeoutSec 2
        $ready = $response.status -eq 'ready'
        if (-not $ready) { Write-Verbose "Voice health returned status '$($response.status)'." }
        return $ready
    }
    catch { Write-Verbose "Voice health probe failed: $($_.Exception.Message)"; return $false }
}

function Get-OwnedVoiceWorker {
    if (-not (Test-Path -LiteralPath $pidPath -PathType Leaf)) { return $null }
    try {
        $state = Get-Content -LiteralPath $pidPath -Raw | ConvertFrom-Json
        $candidatePid = [int]$state.pid
        $expectedStart = [DateTimeOffset]::Parse([string]$state.processStartTimeUtc, [Globalization.CultureInfo]::InvariantCulture)
        if ($candidatePid -le 0 -or
            -not ([IO.Path]::GetFullPath([string]$state.python)).Equals([IO.Path]::GetFullPath($runtimePython), [StringComparison]::OrdinalIgnoreCase) -or
            -not ([IO.Path]::GetFullPath([string]$state.workerScript)).Equals([IO.Path]::GetFullPath($workerScript), [StringComparison]::OrdinalIgnoreCase) -or
            -not ([IO.Path]::GetFullPath([string]$state.manifest)).Equals([IO.Path]::GetFullPath($runtimeManifest), [StringComparison]::OrdinalIgnoreCase) -or
            -not ([IO.Path]::GetFullPath([string]$state.modelLock)).Equals([IO.Path]::GetFullPath($modelLock), [StringComparison]::OrdinalIgnoreCase) -or
            [int]$state.port -ne $WorkerPort) {
            return $null
        }
        $candidate = Get-Process -Id $candidatePid -ErrorAction SilentlyContinue
        if (-not $candidate) { return $null }
        $actualStart = [DateTimeOffset]$candidate.StartTime.ToUniversalTime()
        if ([Math]::Abs(($actualStart - $expectedStart).TotalSeconds) -gt 2) { return $null }
        if (-not ([IO.Path]::GetFullPath($candidate.Path)).Equals([IO.Path]::GetFullPath($runtimePython), [StringComparison]::OrdinalIgnoreCase)) { return $null }

        # PID reuse is not ownership. Require the exact Python executable,
        # start time, and worker arguments before this script may reuse or stop
        # the process. If command-line inspection is unavailable, fail closed.
        $details = Get-CimInstance Win32_Process -Filter "ProcessId = $candidatePid" -ErrorAction Stop
        $commandLine = [string]$details.CommandLine
        if ([string]::IsNullOrWhiteSpace($commandLine) -or
            $commandLine.IndexOf($workerScript, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
            $commandLine.IndexOf($runtimeManifest, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
            $commandLine.IndexOf($modelLock, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            return $null
        }
        return $candidate
    }
    catch { return $null }
}

$currentProcess = Get-OwnedVoiceWorker
if ($currentProcess -and -not $Restart -and (Test-WorkerHealth -WorkerToken $token)) {
    Write-Host "Framewright voice worker is healthy as process $($currentProcess.Id). The oracle was merely being quiet."
    exit 0
}
# A healthy authenticated worker launched from another Framewright checkout or
# an immediately preceding package revision is safe to reuse. Without the exact
# PID ownership proof it is never safe to stop that process, even for -Restart.
if (-not $currentProcess -and (Test-WorkerHealth -WorkerToken $token)) {
    Write-Warning 'A healthy authenticated Framewright voice worker already owns this port; reusing it without touching its process.'
    exit 0
}
if ($currentProcess) {
    Stop-Process -Id $currentProcess.Id -Force
    $currentProcess.WaitForExit(5000)
}
# Invalid JSON, an exited process, or a reused PID is stale bookkeeping. Never
# stop a process that failed the ownership proof above.
Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue

function ConvertTo-WindowsProcessArgument([string]$Value) {
    if ($Value -notmatch '[\s"]') { return $Value }
    # These generated paths never end in a separator; standard quoted Windows
    # argv escaping is therefore sufficient and keeps packaged paths under
    # "Program Files" from being split into accidental arguments.
    return '"' + $Value.Replace('"', '\"') + '"'
}
$previousToken = $env:FRAMEWRIGHT_VOICE_WORKER_TOKEN
try {
    $env:FRAMEWRIGHT_VOICE_WORKER_TOKEN = $token
    $arguments = @(
        $workerScript,
        '--packages', $runtimePackages,
        '--manifest', $runtimeManifest,
        '--lock', $modelLock,
        '--python', $runtimePython,
        '--host', '0.0.0.0',
        '--port', $WorkerPort.ToString([Globalization.CultureInfo]::InvariantCulture)
    )
    $argumentLine = ($arguments | ForEach-Object { ConvertTo-WindowsProcessArgument ([string]$_) }) -join ' '
    $process = Start-Process -FilePath $runtimePython -ArgumentList $argumentLine -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    $processStart = ([DateTimeOffset]$process.StartTime.ToUniversalTime()).ToString('O')
    $pidState = [ordered]@{
        schemaVersion = 1
        pid = $process.Id
        processStartTimeUtc = $processStart
        python = [IO.Path]::GetFullPath($runtimePython)
        workerScript = [IO.Path]::GetFullPath($workerScript)
        manifest = [IO.Path]::GetFullPath($runtimeManifest)
        modelLock = [IO.Path]::GetFullPath($modelLock)
        port = $WorkerPort
    } | ConvertTo-Json
    [System.IO.File]::WriteAllText($pidPath, $pidState, [System.Text.UTF8Encoding]::new($false))
}
finally {
    $env:FRAMEWRIGHT_VOICE_WORKER_TOKEN = $previousToken
}

try {
    # Worker startup verifies roughly 9 GB of pinned model data before exposing
    # health. Permit that one-time integrity pass on a cold disk without
    # pretending an unverified process is ready.
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $healthy = $false
    while (-not $healthy -and [DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 250
        $healthy = Test-WorkerHealth -WorkerToken $token
    }
    if (-not $healthy) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        $detail = if (Test-Path -LiteralPath $stderrPath) { (Get-Content -LiteralPath $stderrPath -Raw).Trim() } else { 'No diagnostic was written.' }
        throw "Framewright voice worker did not become healthy: $detail"
    }
}
catch {
    Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
    throw
}
Write-Host "Framewright voice worker is ready as process $($process.Id). The local oracle now has a proper front door."

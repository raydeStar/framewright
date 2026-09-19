[CmdletBinding()]
param(
    [ValidateRange(1, 65535)]
    [int]$Port = 5179,
    [string]$VoiceRuntimeRoot = (Join-Path $env:LOCALAPPDATA 'FramewrightVoice'),
    [switch]$SkipVoiceUserEnvironmentUpdate,
    [switch]$SkipBrowser
)

$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$application = Join-Path $installRoot 'Framewright.exe'
$voiceLauncher = Join-Path $PSScriptRoot 'start-voice-worker.ps1'
$runtimeRoot = [IO.Path]::GetFullPath($VoiceRuntimeRoot)
$stateRoot = Join-Path $runtimeRoot 'state'
$stdoutPath = Join-Path $stateRoot 'framewright.stdout.log'
$stderrPath = Join-Path $stateRoot 'framewright.stderr.log'
$pidPath = Join-Path $stateRoot 'framewright.pid'
$url = "http://127.0.0.1:$Port"
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null

function Test-FramewrightLive {
    try {
        $health = Invoke-RestMethod -Uri "$url/health/live" -TimeoutSec 2
        return $health.status -in @('Live', 'Ready', 'Degraded', 'live', 'ready', 'degraded')
    }
    catch { return $false }
}

if (-not (Test-Path -LiteralPath $application -PathType Leaf)) {
    throw "Installed Framewright executable is missing: $application"
}

if (-not (Test-FramewrightLive)) {
    if (Test-Path -LiteralPath $voiceLauncher -PathType Leaf) {
        try {
            # The script writes the authenticated endpoint/token into this
            # process environment before returning, so the immediately launched
            # API receives first-run voice configuration without a new login.
            & $voiceLauncher -VoiceRuntimeRoot $runtimeRoot -SkipUserEnvironmentUpdate:$SkipVoiceUserEnvironmentUpdate
        }
        catch {
            Write-Warning "Optional local voice is unavailable: $($_.Exception.Message)"
            Write-Warning 'Framewright will open in degraded mode; image, board, review, and export remain available.'
        }
    }

    $env:Urls = $url
    $process = Start-Process -FilePath $application -WorkingDirectory $installRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    $state = [ordered]@{
        schemaVersion = 1
        pid = $process.Id
        processStartTimeUtc = ([DateTimeOffset]$process.StartTime.ToUniversalTime()).ToString('O')
        executable = [IO.Path]::GetFullPath($application)
        url = $url
    } | ConvertTo-Json
    [IO.File]::WriteAllText($pidPath, $state, [Text.UTF8Encoding]::new($false))

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    while (-not $process.HasExited -and -not (Test-FramewrightLive) -and [DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    if (-not (Test-FramewrightLive)) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        $detail = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) { (Get-Content -LiteralPath $stderrPath -Tail 20) -join [Environment]::NewLine } else { 'No diagnostic was written.' }
        throw "Installed Framewright did not become live. See $stderrPath.`n$detail"
    }
}

if (-not $SkipBrowser) {
    Start-Process $url
}
Write-Host "Framewright is live at $url. The workshop door is open."

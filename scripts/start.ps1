[CmdletBinding()]
param(
    [switch]$Native,
    [switch]$Rebuild,
    [switch]$ProbeOnly,
    [switch]$OpenSetup,
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$scriptRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptRoot

function Test-LocalVoiceEnabled {
    if ($env:FRAMEWRIGHT_LOCAL_VOICE_ENABLED -match '^(?i:true|1|yes)$') { return $true }
    $environmentPath = Join-Path $repoRoot '.env'
    if (Test-Path -LiteralPath $environmentPath -PathType Leaf) {
        $entry = [IO.File]::ReadAllLines($environmentPath) | Where-Object { $_ -match '^FRAMEWRIGHT_LOCAL_VOICE_ENABLED=' } | Select-Object -Last 1
        if ($entry -and ($entry.Split('=', 2)[1] -match '^(?i:true|1|yes)$')) { return $true }
    }
    $settingsPath = Join-Path $repoRoot 'appsettings.Local.json'
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        try { return (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).QwenTts.Enabled -eq $true }
        catch { Write-Warning "Could not read local voice selection from $settingsPath. Voice will remain off." }
    }
    return $false
}

function Test-YuE2Enabled {
    if ($env:FRAMEWRIGHT_YUE2_ENABLED -match '^(?i:true|1|yes)$') { return $true }
    $environmentPath = Join-Path $repoRoot '.env'
    if (Test-Path -LiteralPath $environmentPath -PathType Leaf) {
        $entry = [IO.File]::ReadAllLines($environmentPath) | Where-Object { $_ -match '^FRAMEWRIGHT_YUE2_ENABLED=' } | Select-Object -Last 1
        if ($entry -and ($entry.Split('=', 2)[1] -match '^(?i:true|1|yes)$')) { return $true }
    }
    $settingsPath = Join-Path $repoRoot 'appsettings.Local.json'
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        try { return (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).YuE2.Enabled -eq $true }
        catch { Write-Warning "Could not read YuE2 selection from $settingsPath. YuE2 will remain off." }
    }
    return $false
}

function Invoke-NativeFramewright {
    Write-Host 'Starting Framewright natively. Docker remains politely out of the room.'
    if (Test-LocalVoiceEnabled) {
        try {
            & (Join-Path $scriptRoot 'start-voice-worker.ps1')
        }
        catch {
            Write-Warning "The selected local voice worker is unavailable: $($_.Exception.Message)"
            Write-Warning 'Framewright will continue without local voice. Run .\scripts\setup-voice-worker.ps1 when convenient.'
        }
    }
    else {
        Write-Host 'Local voice was not selected, so its worker remains stopped.'
    }
    if (Test-YuE2Enabled) {
        try { & (Join-Path $scriptRoot 'start-yue2-worker.ps1') }
        catch {
            Write-Warning "The selected YuE2 worker is unavailable: $($_.Exception.Message)"
            Write-Warning 'Framewright will continue without music rendering. Existing audio remains playable.'
        }
    }
    else { Write-Host 'YuE2 was not selected, so its worker remains stopped.' }
    & (Join-Path $scriptRoot 'dev.ps1') -OpenSetup:$OpenSetup -NoBrowser:$NoBrowser
    return $LASTEXITCODE
}

if ($Native) {
    if ($ProbeOnly) { Write-Output 'Native'; exit 0 }
    $nativeExit = Invoke-NativeFramewright
    exit $nativeExit
}

$docker = Get-Command docker -ErrorAction SilentlyContinue
$dockerReady = $false
if ($docker) {
    # PowerShell 5.1 turns native stderr into a terminating error under Stop.
    # Probe Docker out of process so a stopped Desktop cleanly selects native
    # mode instead of aborting the launcher.
    $probe = $null
    try {
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $docker.Source
        $startInfo.Arguments = 'info --format "{{.ServerVersion}}"'
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $probe = [Diagnostics.Process]::Start($startInfo)
        $probe.StandardOutput.ReadToEnd() | Out-Null
        $probe.StandardError.ReadToEnd() | Out-Null
        $probe.WaitForExit()
        $dockerReady = $probe.ExitCode -eq 0
    }
    catch { $dockerReady = $false }
    finally { if ($probe) { $probe.Dispose() } }
}

if ($ProbeOnly) { Write-Output $(if ($dockerReady) { 'Docker' } else { 'Native' }); exit 0 }

if ($dockerReady) {
    $action = if ($Rebuild) { 'rebuild' } else { 'start' }
    Write-Host "Starting Framewright through its managed Docker runtime ($action)."
    & (Join-Path $scriptRoot 'docker.ps1') $action
    $dockerExit = $LASTEXITCODE
    if ($dockerExit -eq 0 -and -not $NoBrowser) {
        $url = if ($OpenSetup) { 'http://127.0.0.1:5179/?setup=1' } else { 'http://127.0.0.1:5179' }
        $ready = $false
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            try {
                Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:5179/health/live' -TimeoutSec 2 | Out-Null
                $ready = $true
                break
            }
            catch { Start-Sleep -Milliseconds 500 }
        }
        if (-not $ready) { Write-Warning 'Framewright did not answer its health check yet; opening the local URL anyway.' }
        Start-Process $url
        Write-Host "Opened $url. The local studio awaits; capes remain optional."
    }
    exit $dockerExit
}

Write-Warning 'Docker Desktop is unavailable; falling back to the native development runtime. Generation assets and the active ComfyUI queue are untouched.'
$nativeExit = Invoke-NativeFramewright
exit $nativeExit

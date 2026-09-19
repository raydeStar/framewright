param(
    [ValidateSet('start', 'stop', 'restart', 'rebuild', 'status', 'logs')]
    [string]$Action = 'start'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$priorBuildEnvironment = @{}
foreach ($name in @('FRAMEWRIGHT_VERSION', 'FRAMEWRIGHT_COMMIT', 'FRAMEWRIGHT_BUILT_AT_UTC', 'FRAMEWRIGHT_CHANNEL')) {
    $priorBuildEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
Push-Location $repoRoot
try {
    if ($Action -eq 'rebuild') {
        $head = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) { throw 'Could not resolve the Git commit for the rebuilt image.' }
        $dirty = @(& git status --porcelain)
        $env:FRAMEWRIGHT_VERSION = if ([string]::IsNullOrWhiteSpace($env:FRAMEWRIGHT_VERSION)) { '0.1.0-rc.1' } else { $env:FRAMEWRIGHT_VERSION }
        $env:FRAMEWRIGHT_COMMIT = if ($dirty.Count -gt 0) { "$head-dirty" } else { $head }
        $env:FRAMEWRIGHT_BUILT_AT_UTC = [DateTimeOffset]::UtcNow.ToString('O')
        $env:FRAMEWRIGHT_CHANNEL = if ([string]::IsNullOrWhiteSpace($env:FRAMEWRIGHT_CHANNEL)) { 'release-candidate' } else { $env:FRAMEWRIGHT_CHANNEL }
    }
    $codexHome = if ([string]::IsNullOrWhiteSpace($env:CODEX_HOME)) { Join-Path $env:USERPROFILE '.codex' } else { $env:CODEX_HOME }
    $environmentPath = Join-Path $repoRoot '.env'
    $voiceSetting = $env:FRAMEWRIGHT_LOCAL_VOICE_ENABLED
    if ([string]::IsNullOrWhiteSpace($voiceSetting) -and (Test-Path -LiteralPath $environmentPath -PathType Leaf)) {
        $voiceEntry = [IO.File]::ReadAllLines($environmentPath) | Where-Object { $_ -match '^FRAMEWRIGHT_LOCAL_VOICE_ENABLED=' } | Select-Object -Last 1
        if ($voiceEntry) { $voiceSetting = $voiceEntry.Split('=', 2)[1] }
    }
    $voiceSelected = $voiceSetting -match '^(?i:true|1|yes)$'
    $voiceReady = $null
    if ($voiceSelected -and $Action -in @('start', 'restart', 'rebuild')) {
        try {
            & (Join-Path $PSScriptRoot 'start-voice-worker.ps1') -Restart:($Action -eq 'restart')
            $voiceReady = $true
        }
        catch {
            $voiceReady = $false
            Write-Warning @"
The optional local voice worker is unavailable: $($_.Exception.Message)
Framewright will still start, and the Setup drawer will report voice as unavailable.
Run .\scripts\setup-voice-worker.ps1 once, then .\scripts\docker.ps1 restart.
"@
        }
    }
    elseif ($Action -in @('start', 'restart', 'rebuild')) {
        Write-Host 'Local voice was not selected, so its worker remains stopped.'
    }

    $yue2Setting = $env:FRAMEWRIGHT_YUE2_ENABLED
    if ([string]::IsNullOrWhiteSpace($yue2Setting) -and (Test-Path -LiteralPath $environmentPath -PathType Leaf)) {
        $yue2Entry = [IO.File]::ReadAllLines($environmentPath) | Where-Object { $_ -match '^FRAMEWRIGHT_YUE2_ENABLED=' } | Select-Object -Last 1
        if ($yue2Entry) { $yue2Setting = $yue2Entry.Split('=', 2)[1] }
    }
    $yue2Selected = $yue2Setting -match '^(?i:true|1|yes)$'
    $yue2Ready = $null
    if ($yue2Selected -and $Action -in @('start', 'restart', 'rebuild')) {
        try {
            & (Join-Path $PSScriptRoot 'start-yue2-worker.ps1') -Restart:($Action -eq 'restart')
            $yue2Ready = $true
        }
        catch {
            $yue2Ready = $false
            Write-Warning "The optional YuE2 worker is unavailable: $($_.Exception.Message)"
            Write-Warning 'Framewright will still start. Existing music remains playable and new renders stay disabled.'
        }
    }
    elseif ($Action -in @('start', 'restart', 'rebuild')) {
        Write-Host 'YuE2 was not selected, so its worker remains stopped.'
    }

    $voiceToken = $env:QwenTts__WorkerToken
    if ([string]::IsNullOrWhiteSpace($voiceToken)) {
        $configuredRuntime = [Environment]::GetEnvironmentVariable('FRAMEWRIGHT_VOICE_RUNTIME_ROOT', 'User')
        $runtimeRoot = if ([string]::IsNullOrWhiteSpace($configuredRuntime)) { Join-Path (Join-Path $env:USERPROFILE 'AppData\Local') 'FramewrightVoice' } else { $configuredRuntime }
        $tokenPath = Join-Path $runtimeRoot 'state\voice-worker.token'
        if (Test-Path -LiteralPath $tokenPath -PathType Leaf) { $voiceToken = ([IO.File]::ReadAllText($tokenPath)).Trim() }
    }
    & (Join-Path $PSScriptRoot 'ensure-docker-env.ps1') `
        -EnvironmentPath $environmentPath -CodexHome $codexHome -VoiceWorkerToken $voiceToken
    $overridePath = Join-Path $repoRoot '.tmp\compose.codex.override.json'
    & (Join-Path $PSScriptRoot 'prepare-codex-compose-override.ps1') -CodexHome $codexHome -OverridePath $overridePath
    $compose = @('compose', '--file', (Join-Path $repoRoot 'compose.yaml'), '--file', $overridePath)

    switch ($Action) {
        'start'   { & docker @compose up --detach }
        'stop'    { & docker @compose down }
        'restart' { & docker @compose up --detach --force-recreate framewright }
        'rebuild' { & docker @compose up --detach --build --force-recreate framewright }
        'status'  { & docker @compose ps }
        'logs'    { & docker @compose logs --follow --tail 120 framewright }
    }

    if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed with exit code $LASTEXITCODE. Even the finest machinery occasionally demands a stern glance." }
    if ($voiceReady -eq $false) {
        Write-Warning 'Framewright is running in degraded mode: image, board, review, and export work remain available; local voice generation is offline.'
    }
    if ($yue2Ready -eq $false) {
        Write-Warning 'Framewright is running in degraded mode: composition records and existing audio remain available; YuE2 planning and rendering are offline.'
    }
}
finally {
    Pop-Location
    foreach ($name in $priorBuildEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $priorBuildEnvironment[$name], 'Process')
    }
}

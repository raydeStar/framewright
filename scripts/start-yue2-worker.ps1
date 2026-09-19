[CmdletBinding()]
param(
    [string]$RuntimeRoot,
    [switch]$Restart
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$runtime = if ([string]::IsNullOrWhiteSpace($RuntimeRoot)) { Join-Path $repoRoot '.yue2-runtime' } else { [IO.Path]::GetFullPath($RuntimeRoot) }
$python = Join-Path $runtime 'Scripts\python.exe'
$worker = Join-Path $repoRoot 'tools\yue2\yue2_service.py'
$state = Join-Path $runtime 'state'
$pidPath = Join-Path $state 'worker.pid'
$stdoutPath = Join-Path $state 'worker.out.log'
$stderrPath = Join-Path $state 'worker.err.log'
$environmentPath = Join-Path $repoRoot '.env'

function Test-YuE2WorkerProcess([int]$ProcessId) {
    $record = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue
    if (-not $record) { return $false }
    $executableMatches = -not [string]::IsNullOrWhiteSpace($record.ExecutablePath) -and
        [IO.Path]::GetFullPath($record.ExecutablePath).Equals([IO.Path]::GetFullPath($python), [StringComparison]::OrdinalIgnoreCase)
    $commandMatches = -not [string]::IsNullOrWhiteSpace($record.CommandLine) -and
        $record.CommandLine.IndexOf($worker, [StringComparison]::OrdinalIgnoreCase) -ge 0
    return $executableMatches -and $commandMatches
}

if (-not (Test-Path -LiteralPath $python -PathType Leaf)) { throw "YuE2 runtime not found at $runtime. Run .\scripts\setup-yue2-worker.ps1 first." }
if (-not (Test-Path -LiteralPath $worker -PathType Leaf)) { throw "YuE2 worker script is missing: $worker" }

if (Test-Path -LiteralPath $environmentPath -PathType Leaf) {
    foreach ($line in [IO.File]::ReadAllLines($environmentPath)) {
        if ($line -notmatch '^(FRAMEWRIGHT_YUE2_[A-Z0-9_]+)=(.*)$') { continue }
        $name = $Matches[1]; $value = $Matches[2]
        switch ($name) {
            'FRAMEWRIGHT_YUE2_WORKER_TOKEN' { $env:YUE2_WORKER_TOKEN = $value }
            'FRAMEWRIGHT_YUE2_MODEL' { $env:YUE2_MODEL = $value }
            'FRAMEWRIGHT_YUE2_VAE' { $env:YUE2_VAE = $value }
            'FRAMEWRIGHT_YUE2_DEVICE' { $env:YUE2_DEVICE = $value }
        }
    }
}
$localSettingsPath = Join-Path $repoRoot 'appsettings.Local.json'
if (Test-Path -LiteralPath $localSettingsPath -PathType Leaf) {
    $local = (Get-Content -LiteralPath $localSettingsPath -Raw | ConvertFrom-Json).YuE2
    if ($local) {
        if ($local.WorkerToken) { $env:YUE2_WORKER_TOKEN = [string]$local.WorkerToken }
        if ($local.Model) { $env:YUE2_MODEL = [string]$local.Model }
        if ($local.Vae) { $env:YUE2_VAE = [string]$local.Vae }
        if ($local.Device) { $env:YUE2_DEVICE = [string]$local.Device }
        if ($local.OutputPath) { $env:YUE2_OUTPUT_PATH = [string]$local.OutputPath }
    }
}
$manifestPath = Join-Path $runtime 'framewright-runtime.json'
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $env:YUE2_ALLOW_DOWNLOADS = ([bool]$manifest.modelDownloadsAllowed).ToString().ToLowerInvariant()
}
if ([string]::IsNullOrWhiteSpace($env:YUE2_OUTPUT_PATH)) { $env:YUE2_OUTPUT_PATH = Join-Path $runtime 'outputs' }
$env:YUE2_HOST = if ([string]::IsNullOrWhiteSpace($env:YUE2_WORKER_TOKEN)) { '127.0.0.1' } else { '0.0.0.0' }
$env:YUE2_PORT = '5182'

if (Test-Path -LiteralPath $pidPath -PathType Leaf) {
    $existingPid = [int]([IO.File]::ReadAllText($pidPath).Trim())
    $existing = Get-Process -Id $existingPid -ErrorAction SilentlyContinue
    if ($existing -and -not (Test-YuE2WorkerProcess $existingPid)) {
        throw "The YuE2 PID file points to unrelated process $existingPid. Refusing to stop it; remove $pidPath after verifying the stale file."
    }
    if ($existing -and -not $Restart) { Write-Host "YuE2 worker is already running as PID $existingPid."; exit 0 }
    if ($existing) { Stop-Process -Id $existingPid -Force; $existing.WaitForExit(10000) }
    Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $state -Force | Out-Null
$process = Start-Process -FilePath $python -ArgumentList @($worker) -WorkingDirectory $repoRoot -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
[IO.File]::WriteAllText($pidPath, [string]$process.Id, [Text.UTF8Encoding]::new($false))
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    try {
        $headers = @{}; if ($env:YUE2_WORKER_TOKEN) { $headers['X-Framewright-Worker-Token'] = $env:YUE2_WORKER_TOKEN }
        $health = Invoke-RestMethod -Method Get -Uri 'http://127.0.0.1:5182/health' -Headers $headers -TimeoutSec 2
        if ($health.ready) { Write-Host "YuE2 worker is ready as PID $($process.Id). The GPU remains idle until you choose Compose or Render."; exit 0 }
        throw $health.detail
    }
    catch { Start-Sleep -Milliseconds 500 }
}
if (-not $process.HasExited -and (Test-YuE2WorkerProcess $process.Id)) {
    Stop-Process -Id $process.Id -Force
    $process.WaitForExit(10000)
}
Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
throw "YuE2 worker did not become ready. Review $stderrPath. No generation job was submitted."

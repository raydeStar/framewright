[CmdletBinding()]
param([string]$RuntimeRoot)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$runtime = if ([string]::IsNullOrWhiteSpace($RuntimeRoot)) { Join-Path $repoRoot '.yue2-runtime' } else { [IO.Path]::GetFullPath($RuntimeRoot) }
$pidPath = Join-Path $runtime 'state\worker.pid'
if (-not (Test-Path -LiteralPath $pidPath -PathType Leaf)) { Write-Host 'YuE2 worker is already stopped.'; exit 0 }
$workerPid = [int]([IO.File]::ReadAllText($pidPath).Trim())
$process = Get-Process -Id $workerPid -ErrorAction SilentlyContinue
if ($process) {
    $python = Join-Path $runtime 'Scripts\python.exe'
    $worker = Join-Path $repoRoot 'tools\yue2\yue2_service.py'
    $record = Get-CimInstance Win32_Process -Filter "ProcessId = $workerPid" -ErrorAction SilentlyContinue
    $owned = $record -and -not [string]::IsNullOrWhiteSpace($record.ExecutablePath) -and
        [IO.Path]::GetFullPath($record.ExecutablePath).Equals([IO.Path]::GetFullPath($python), [StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::IsNullOrWhiteSpace($record.CommandLine) -and
        $record.CommandLine.IndexOf($worker, [StringComparison]::OrdinalIgnoreCase) -ge 0
    if (-not $owned) { throw "The YuE2 PID file points to unrelated process $workerPid. Refusing to stop it; the quiet order does not assassinate bystanders." }
    Stop-Process -Id $workerPid
    $process.WaitForExit(30000)
}
Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
Write-Host 'YuE2 worker stopped. Saved compositions, revisions, renders, and model files were preserved.'

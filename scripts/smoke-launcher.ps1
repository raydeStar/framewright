[CmdletBinding()]
param(
    [string]$ArtifactPath,
    [ValidateRange(1024, 65535)]
    [int]$Port = 5232
)

<#
  Proves the double-clickable launcher on a published package, on a disposable
  data root and port: it starts the packaged studio hidden, waits for it, writes
  the same state file the PowerShell launcher writes, and a second launch opens
  the running studio instead of starting another. The artist's own studio,
  data and port are never touched. "--stop" then stops exactly that studio.
#>
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($ArtifactPath)) { $ArtifactPath = Join-Path $repoRoot 'artifacts\framewright-win-x64' }
$artifact = [IO.Path]::GetFullPath($ArtifactPath)
$launcher = Join-Path $artifact 'Framewright Studio.exe'
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) { throw "Launcher missing in $artifact. Publish first." }

$root = Join-Path ([IO.Path]::GetTempPath()) ("framewright-launcher-smoke-{0}" -f [Guid]::NewGuid().ToString('N'))
$url = "http://127.0.0.1:$Port"
$saved = @{}
foreach ($name in 'Studio__DataRoot', 'FRAMEWRIGHT_PORT', 'FRAMEWRIGHT_VOICE_RUNTIME', 'FRAMEWRIGHT_LAUNCHER_NO_BROWSER', 'FRAMEWRIGHT_LAUNCHER_NO_DIALOG') {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}
$studio = $null
try {
    try { Invoke-RestMethod "$url/health/live" -TimeoutSec 2 | Out-Null; throw "Port $Port is already serving something; choose another." } catch [System.Net.WebException] { }
    New-Item -ItemType Directory -Path (Join-Path $root 'data') -Force | Out-Null
    $env:Studio__DataRoot = Join-Path $root 'data'
    $env:FRAMEWRIGHT_PORT = "$Port"
    $env:FRAMEWRIGHT_VOICE_RUNTIME = Join-Path $root 'voice'
    $env:FRAMEWRIGHT_LAUNCHER_NO_BROWSER = '1'
    $env:FRAMEWRIGHT_LAUNCHER_NO_DIALOG = '1'

    # --stop must spare a process the state file names but this folder did not
    # start: a reused process id, or anything that is not its Framewright.exe.
    New-Item -ItemType Directory -Path (Join-Path $root 'voice\state') -Force | Out-Null
    $bystander = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 60' -PassThru -WindowStyle Hidden
    try {
        $forged = [ordered]@{ schemaVersion = 1; pid = $bystander.Id; processStartTimeUtc = $bystander.StartTime.ToUniversalTime().ToString('O'); executable = (Join-Path $artifact 'Framewright.exe'); url = $url }
        [IO.File]::WriteAllText((Join-Path $root 'voice\state\framewright.pid'), ($forged | ConvertTo-Json))
        $spare = Start-Process -FilePath $launcher -ArgumentList '--stop' -PassThru -WindowStyle Hidden
        if (-not $spare.WaitForExit(30000) -or $spare.ExitCode -ne 0) { throw 'Stopping with a foreign state file should be a quiet no-op.' }
        $bystander.Refresh()
        if ($bystander.HasExited) { throw '--stop killed a process that was not the studio from this folder.' }
    }
    finally { if (-not $bystander.HasExited) { Stop-Process -Id $bystander.Id -Force } }
    Remove-Item -LiteralPath (Join-Path $root 'voice\state\framewright.pid') -Force

    # Wait on the launcher alone: Start-Process -Wait also waits for the studio
    # the launcher leaves running, which is the whole point of the launcher.
    # Its output is read through a pipe, as a script or scheduled task would: the
    # studio must not inherit that pipe, or the reader waits forever.
    $start = [Diagnostics.ProcessStartInfo]::new($launcher)
    $start.UseShellExecute = $false; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true; $start.CreateNoWindow = $true
    $first = [Diagnostics.Process]::Start($start)
    $firstOutput = $first.StandardOutput.ReadToEndAsync()
    $firstErrors = $first.StandardError.ReadToEndAsync()
    if (-not $first.WaitForExit(120000)) { throw 'The launcher did not return within two minutes.' }
    if (-not $firstOutput.Wait(10000) -or -not $firstErrors.Wait(1000)) { throw 'The studio kept the launcher''s output pipe open; a script reading it would never finish.' }
    if ($firstOutput.Result -notmatch 'is live at') { throw "The launcher did not report the live studio: $($firstOutput.Result) $($firstErrors.Result)" }
    if ($first.ExitCode -ne 0) { throw "The launcher exited with $($first.ExitCode). See $root\voice\state\framewright.stderr.log" }
    $health = Invoke-RestMethod "$url/health/live" -TimeoutSec 5
    if ($health.status -notmatch '^(?i)(live|ready|degraded)$') { throw "The launched studio reported $($health.status)." }
    $state = Get-Content -LiteralPath (Join-Path $root 'voice\state\framewright.pid') -Raw | ConvertFrom-Json
    $studio = Get-Process -Id ([int]$state.pid) -ErrorAction Stop
    if ([IO.Path]::GetFullPath($studio.Path) -ne [IO.Path]::GetFullPath((Join-Path $artifact 'Framewright.exe'))) {
        throw "The state file names a process that is not the packaged studio: $($studio.Path)"
    }
    # The launcher has exited; the studio it started keeps running and logging.
    if (-not (Test-Path -LiteralPath (Join-Path $root 'voice\state\framewright.stdout.log'))) { throw 'The studio is not writing its log.' }

    $second = Start-Process -FilePath $launcher -PassThru -WindowStyle Hidden
    if (-not $second.WaitForExit(30000)) { throw 'A second launch did not return promptly.' }
    if ($second.ExitCode -ne 0) { throw "A second launch exited with $($second.ExitCode)." }
    $running = @(Get-Process -Name 'Framewright' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and [IO.Path]::GetFullPath($_.Path) -eq [IO.Path]::GetFullPath((Join-Path $artifact 'Framewright.exe')) })
    if ($running.Count -ne 1) { throw "A second launch started another studio ($($running.Count) running)." }

    $stop = Start-Process -FilePath $launcher -ArgumentList '--stop' -PassThru -WindowStyle Hidden
    if (-not $stop.WaitForExit(30000) -or $stop.ExitCode -ne 0) { throw 'Stopping the studio through the launcher failed.' }
    if (-not $studio.WaitForExit(5000)) { throw 'The launcher reported a stop but the studio is still running.' }
    if (Test-Path -LiteralPath (Join-Path $root 'voice\state\framewright.pid')) { throw 'A stopped studio left its state file behind.' }
    $again = Start-Process -FilePath $launcher -ArgumentList '--stop' -PassThru -WindowStyle Hidden
    if (-not $again.WaitForExit(30000) -or $again.ExitCode -ne 0) { throw 'Stopping an already stopped studio should be a quiet no-op.' }
    Write-Host "Launcher smoke passed: hidden start, health, state file, logging, a second launch reusing the running studio, and --stop ($url)."
}
finally {
    if ($studio -and -not $studio.HasExited) { Stop-Process -Id $studio.Id -Force; $studio.WaitForExit(10000) | Out-Null }
    elseif (-not $studio -and (Test-Path -LiteralPath (Join-Path $root 'voice\state\framewright.pid'))) {
        # A check failed before the studio was recorded; stop what this run started.
        $cleanup = Start-Process -FilePath $launcher -ArgumentList '--stop' -PassThru -WindowStyle Hidden
        $cleanup.WaitForExit(30000) | Out-Null
    }
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    if ((Split-Path -Leaf $root).StartsWith('framewright-launcher-smoke-') -and (Test-Path -LiteralPath $root)) {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}

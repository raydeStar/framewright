[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EnvironmentPath,
    [Parameter(Mandatory = $true)]
    [string]$CodexHome,
    [string]$VoiceWorkerToken
)

$ErrorActionPreference = 'Stop'
$resolvedPath = [IO.Path]::GetFullPath($EnvironmentPath)
$resolvedCodexHome = [IO.Path]::GetFullPath($CodexHome)
$parent = Split-Path -Parent $resolvedPath
if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}
$lines = if (Test-Path -LiteralPath $resolvedPath -PathType Leaf) { [IO.File]::ReadAllLines($resolvedPath) } else { @() }
$dockerPath = $resolvedCodexHome.Replace('\', '/')
$desiredValues = [ordered]@{ FRAMEWRIGHT_CODEX_HOME = $dockerPath }
if (-not [string]::IsNullOrWhiteSpace($VoiceWorkerToken)) {
    $desiredValues.FRAMEWRIGHT_VOICE_WORKER_TOKEN = $VoiceWorkerToken
}
$changed = $false
foreach ($entry in $desiredValues.GetEnumerator()) {
    $pattern = "^$([regex]::Escape([string]$entry.Key))="
    $desired = "$($entry.Key)=$($entry.Value)"
    $existing = @($lines | Where-Object { $_ -match $pattern })
    if ($existing.Count -ne 1 -or $existing[0] -ne $desired) {
        $lines = @($lines | Where-Object { $_ -notmatch $pattern }) + $desired
        $changed = $true
    }
}
if ($changed -or -not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
    [IO.File]::WriteAllLines($resolvedPath, $lines, [Text.UTF8Encoding]::new($false))
    Write-Host 'Updated the ignored Docker environment with the workstation Codex home. Secrets remain outside the repository.'
}

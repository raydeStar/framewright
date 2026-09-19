[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CodexHome,
    [Parameter(Mandatory = $true)]
    [string]$OverridePath
)

$ErrorActionPreference = 'Stop'
$resolvedHome = [IO.Path]::GetFullPath($CodexHome)
$resolvedOverride = [IO.Path]::GetFullPath($OverridePath)
$parent = Split-Path -Parent $resolvedOverride
New-Item -ItemType Directory -Path $parent -Force | Out-Null

$volumes = New-Object System.Collections.Generic.List[object]
$auth = Join-Path $resolvedHome 'auth.json'
$imageSkill = Join-Path $resolvedHome 'skills\.system\imagegen'
if (Test-Path -LiteralPath $auth -PathType Leaf) {
    $volumes.Add([ordered]@{
        type = 'bind'
        source = $auth
        target = '/codex-seed/auth.json'
        read_only = $true
        bind = [ordered]@{ create_host_path = $false }
    })
}
if (Test-Path -LiteralPath $imageSkill -PathType Container) {
    $volumes.Add([ordered]@{
        type = 'bind'
        source = $imageSkill
        target = '/codex-seed/skills/imagegen'
        read_only = $true
        bind = [ordered]@{ create_host_path = $false }
    })
}

$service = [ordered]@{}
if ($volumes.Count -gt 0) { $service['volumes'] = $volumes.ToArray() }
$document = [ordered]@{ services = [ordered]@{ framewright = $service } }
[IO.File]::WriteAllText($resolvedOverride, ($document | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
if ($volumes.Count -eq 0) {
    Write-Warning 'Codex login or ImageGen skill is absent. Framewright will start; Codex ImageGen will remain visibly unavailable.'
} elseif ($volumes.Count -lt 2) {
    Write-Warning 'Only part of the Codex seed is available. Framewright will start, but Codex ImageGen may remain unavailable.'
}

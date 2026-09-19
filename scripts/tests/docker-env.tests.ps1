[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$testBase = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\script-tests'))
New-Item -ItemType Directory -Path $testBase -Force | Out-Null
$testRoot = Join-Path $testBase ("framewright-env-test-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $path = Join-Path $testRoot '.env'
    $token = 'FRAMEWRIGHT_VOICE_WORKER_TOKEN=already-present-test-token'
    [IO.File]::WriteAllLines($path, @('# preserved comment', $token), [Text.UTF8Encoding]::new($false))
    $codexHome = Join-Path $testRoot 'Codex Home'
    & (Join-Path $repoRoot 'scripts\ensure-docker-env.ps1') -EnvironmentPath $path -CodexHome $codexHome
    & (Join-Path $repoRoot 'scripts\ensure-docker-env.ps1') -EnvironmentPath $path -CodexHome $codexHome
    $lines = [IO.File]::ReadAllLines($path)
    if (@($lines | Where-Object { $_ -eq $token }).Count -ne 1) { throw 'Docker environment initialization changed or duplicated the voice token.' }
    $codexLines = @($lines | Where-Object { $_ -match '^FRAMEWRIGHT_CODEX_HOME=' })
    if ($codexLines.Count -ne 1 -or $codexLines[0] -notmatch '/Codex Home$') { throw 'Docker environment initialization did not upsert exactly one Codex home.' }
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { throw 'Docker environment initialization wrote a UTF-8 BOM.' }
    $override = Join-Path $testRoot 'compose.override.json'
    & (Join-Path $repoRoot 'scripts\prepare-codex-compose-override.ps1') -CodexHome $codexHome -OverridePath $override -WarningAction SilentlyContinue
    if (Test-Path -LiteralPath $codexHome) { throw 'Preparing a degraded Docker launch created or mutated the missing Codex home.' }
    $emptyOverride = Get-Content -LiteralPath $override -Raw | ConvertFrom-Json
    if ($emptyOverride.services.framewright.PSObject.Properties.Name -contains 'volumes') {
        throw 'A missing Codex login produced an invalid bind mount instead of degraded startup.'
    }

    New-Item -ItemType Directory -Path (Join-Path $codexHome 'skills\.system\imagegen') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $codexHome 'auth.json'), '{}', [Text.UTF8Encoding]::new($false))
    & (Join-Path $repoRoot 'scripts\prepare-codex-compose-override.ps1') -CodexHome $codexHome -OverridePath $override
    $readyOverride = Get-Content -LiteralPath $override -Raw | ConvertFrom-Json
    if (@($readyOverride.services.framewright.volumes).Count -ne 2) {
        throw 'A complete Codex home did not produce the two explicit read-only seed mounts.'
    }
    Write-Host 'Docker environment test passed: token merge is stable and missing Codex files degrade without path mutation.'
}
finally {
    $resolvedTest = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTest.StartsWith($testBase, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTest).StartsWith('framewright-env-test-', [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTest)) {
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force
    }
}

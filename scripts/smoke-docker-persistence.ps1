param(
    [string]$BaseUrl = 'http://127.0.0.1:5179'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Get-Fingerprint([string]$url) {
    $studio = Invoke-RestMethod -Uri "$url/api/studio" -TimeoutSec 20
    $assetResponse = Invoke-RestMethod -Uri "$url/api/assets?includeArchived=true" -TimeoutSec 30
    $assets = @($assetResponse)
    return [ordered]@{
        projectId = $studio.project.id
        projectName = $studio.project.name
        projectUpdatedAt = $studio.project.updatedAt
        shotIds = @($studio.shots | ForEach-Object { $_.id } | Sort-Object)
        referenceIds = @($studio.references | ForEach-Object { $_.id } | Sort-Object)
        commentIds = @($studio.comments | ForEach-Object { $_.id } | Sort-Object)
        assetIds = @($assets | ForEach-Object { $_.id } | Sort-Object)
    }
}

function Wait-Healthy([string]$url) {
    $deadline = (Get-Date).AddMinutes(2)
    while ((Get-Date) -lt $deadline) {
        try {
            $health = Invoke-RestMethod -Uri "$url/health/ready" -TimeoutSec 3
            if ($health.status -in @('Ready', 'Degraded')) { return }
        }
        catch { Start-Sleep -Milliseconds 750 }
    }
    throw "Framewright did not become healthy within two minutes after the container restart."
}

Push-Location $repoRoot
try {
    Wait-Healthy $BaseUrl
    docker exec framewright sh -lc 'test -w "$CODEX_HOME"'
    if ($LASTEXITCODE -ne 0) { throw "The unprivileged Framewright process cannot write its isolated Codex runtime home." }
    docker exec framewright sh -lc 'test -n "$HOME" -a -w "$HOME"'
    if ($LASTEXITCODE -ne 0) { throw "The unprivileged Framewright process does not have a writable operating-system home." }
    $before = Get-Fingerprint $BaseUrl
    docker compose restart framewright
    if ($LASTEXITCODE -ne 0) { throw "Docker could not restart Framewright." }
    Wait-Healthy $BaseUrl
    $after = Get-Fingerprint $BaseUrl
    $beforeJson = $before | ConvertTo-Json -Depth 8 -Compress
    $afterJson = $after | ConvertTo-Json -Depth 8 -Compress
    if ($beforeJson -ne $afterJson) {
        throw "Persistence fingerprint changed across restart.`nBefore: $beforeJson`nAfter:  $afterJson"
    }
    Write-Host "Docker restart persistence passed: $($after.shotIds.Count) shots, $($after.referenceIds.Count) authorities, $($after.assetIds.Count) assets, and $($after.commentIds.Count) notes survived exactly."
}
finally {
    Pop-Location
}

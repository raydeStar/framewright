[CmdletBinding()]
param(
    [string]$Version = '0.1.0-rc.1',
    [string]$Channel = 'release-candidate',
    [uri]$BaseUrl = 'http://127.0.0.1:5179',
    [uri]$ComfyUiUrl = 'http://127.0.0.1:8188',
    [switch]$SkipAutomatedVerification,
    [switch]$SkipWindowsPackage
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Import-Module (Join-Path $PSScriptRoot 'ReleaseCandidate.psm1') -Force
$priorVersion = $env:FRAMEWRIGHT_VERSION
$priorChannel = $env:FRAMEWRIGHT_CHANNEL

function Assert-NativeSuccess([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step failed with exit code $LASTEXITCODE. The candidate remains safely uncut." }
}

function Wait-FramewrightReady([uri]$Url) {
    $deadline = [DateTimeOffset]::Now.AddMinutes(3)
    while ([DateTimeOffset]::Now -lt $deadline) {
        try {
            $health = Invoke-RestMethod -Uri "$($Url.AbsoluteUri.TrimEnd('/'))/health/ready" -TimeoutSec 5
            if ($health.status -in @('Ready', 'Degraded')) { return $health }
        }
        catch { Start-Sleep -Milliseconds 750 }
    }
    throw 'Framewright did not report operational readiness within three minutes.'
}

function Get-StudioFingerprint([uri]$Url) {
    $base = $Url.AbsoluteUri.TrimEnd('/')
    $studio = Invoke-RestMethod -Uri "$base/api/studio" -TimeoutSec 20
    # PowerShell 7 emits a top-level JSON array from Invoke-RestMethod as one
    # pipeline object when the command is nested directly inside @(...). Keep
    # the response assignment separate so the array is flattened correctly.
    $assetResponse = Invoke-RestMethod -Uri "$base/api/assets?includeArchived=true" -TimeoutSec 30
    $assets = @($assetResponse)
    $maintenance = Invoke-RestMethod -Uri "$base/api/maintenance/status" -TimeoutSec 30
    $physicalAssetRoot = Join-Path $repoRoot 'src\StoryboardStudio.Api\App_Data\assets'
    $physicalAssets = if (Test-Path -LiteralPath $physicalAssetRoot -PathType Container) {
        @(Get-ChildItem -LiteralPath $physicalAssetRoot -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/]\.staging[\\/]' } | Sort-Object FullName)
    }
    else { @() }
    $physicalInventory = @($physicalAssets | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($physicalAssetRoot, $_.FullName).Replace('\', '/')
        "${relative}:$($_.Length):$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"
    })
    $canonical = [ordered]@{
        projectId = $studio.project.id
        shots = @($studio.shots | ForEach-Object { "$($_.id):$($_.version):$($_.currentImageAssetId):$($_.productionVideoAssetId)" } | Sort-Object)
        references = @($studio.references | ForEach-Object { "$($_.id):$($_.version):$($_.imageAssetId)" } | Sort-Object)
        comments = @($studio.comments | ForEach-Object { "$($_.id):$($_.version):$($_.state)" } | Sort-Object)
        jobs = @($studio.jobs | ForEach-Object { "$($_.id):$($_.state):$($_.outputAssetId):$($_.providerRequestId)" } | Sort-Object)
        assets = @($assets | ForEach-Object { "$($_.id):$($_.contentHash):$($_.bytes):$($_.isArchived)" } | Sort-Object)
        databaseInventory = "$($maintenance.databaseIntegrity):$($maintenance.assetCount):$($maintenance.assetBytes)"
        physicalAssetInventory = $physicalInventory
    } | ConvertTo-Json -Depth 8 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($canonical)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    return [pscustomobject]@{
        Hash = $hash
        Studio = $studio
        Counts = [ordered]@{
            shots = @($studio.shots).Count
            references = @($studio.references).Count
            comments = @($studio.comments).Count
            jobs = @($studio.jobs).Count
            activeProjectAssets = $assets.Count
            databaseAssets = [int]$maintenance.assetCount
            physicalAssetFiles = $physicalAssets.Count
            physicalAssetBytes = [long](($physicalAssets | Measure-Object Length -Sum).Sum ?? 0)
        }
    }
}

Push-Location $repoRoot
try {
    $commit = (& git rev-parse HEAD).Trim(); Assert-NativeSuccess 'Git commit lookup'
    $upstream = (& git rev-parse --abbrev-ref --symbolic-full-name '@{u}').Trim(); Assert-NativeSuccess 'Git upstream lookup'
    $dirty = @(& git status --porcelain)
    $distance = (& git rev-list --left-right --count "HEAD...$upstream").Trim() -split '\s+'
    Assert-NativeSuccess 'Git upstream comparison'
    Assert-ReleaseRepositoryState -DirtyPaths $dirty -Ahead ([int]$distance[0]) -Behind ([int]$distance[1])

    $before = Get-StudioFingerprint -Url $BaseUrl
    Assert-NoActiveFramewrightJobs -Jobs @($before.Studio.jobs)
    $comfyQueueVerifiedEmpty = $false
    $comfyQueueUnavailable = $null
    try {
        $comfyQueue = Invoke-RestMethod -Uri "$($ComfyUiUrl.AbsoluteUri.TrimEnd('/'))/queue" -TimeoutSec 15
        Assert-NoActiveComfyUiJobs -Queue $comfyQueue
        $comfyQueueVerifiedEmpty = $true
    }
    catch {
        if ($_.Exception.Message -match 'ComfyUI is busy') { throw }
        $comfyQueueUnavailable = $_.Exception.Message
        Write-Warning "ComfyUI queue inspection is unavailable and will be recorded as a manual QA blocker: $comfyQueueUnavailable"
    }

    $stamp = [DateTimeOffset]::UtcNow
    $shortCommit = $commit.Substring(0, [Math]::Min(12, $commit.Length))
    $artifactRoot = Join-Path $repoRoot ("artifacts\release\{0}-{1}-{2}" -f $Version, $shortCommit, $stamp.ToString('yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

    $backupPath = Join-Path $artifactRoot 'framewright-pre-cutover-backup.zip'
    Invoke-WebRequest -Uri "$($BaseUrl.AbsoluteUri.TrimEnd('/'))/api/maintenance/backup" -OutFile $backupPath -TimeoutSec 600
    $backupEvidence = Test-FramewrightBackupArchive -Path $backupPath

    if (-not $SkipAutomatedVerification) {
        & (Join-Path $PSScriptRoot 'verify.ps1')
        Assert-NativeSuccess 'Automated release gate'
    }

    $builtAt = [DateTimeOffset]::UtcNow.ToString('O')
    if (-not $SkipWindowsPackage) {
        $packagePath = Join-Path $repoRoot 'artifacts\framewright-win-x64'
        & (Join-Path $PSScriptRoot 'publish-local.ps1') -OutputPath $packagePath -Version $Version -Commit $commit -BuiltAtUtc $builtAt -Channel $Channel
        Assert-NativeSuccess 'Windows package publish'
        & (Join-Path $PSScriptRoot 'smoke-package.ps1') -ArtifactPath $packagePath
        Assert-NativeSuccess 'Windows package smoke'
        & (Join-Path $PSScriptRoot 'smoke-installer.ps1') -ArtifactPath $packagePath
        Assert-NativeSuccess 'Windows installer rollback smoke'
    }

    $env:FRAMEWRIGHT_VERSION = $Version
    $env:FRAMEWRIGHT_CHANNEL = $Channel
    & (Join-Path $PSScriptRoot 'docker.ps1') rebuild
    Assert-NativeSuccess 'Framewright-only Docker rebuild'
    $ready = Wait-FramewrightReady -Url $BaseUrl

    $health = Invoke-RestMethod -Uri "$($BaseUrl.AbsoluteUri.TrimEnd('/'))/health/live" -TimeoutSec 10
    $imageId = (& docker inspect framewright --format '{{.Image}}').Trim(); Assert-NativeSuccess 'Container image lookup'
    $imageCommit = (& docker image inspect $imageId --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}').Trim(); Assert-NativeSuccess 'Image revision lookup'
    $imageVersion = (& docker image inspect $imageId --format '{{ index .Config.Labels "org.opencontainers.image.version" }}').Trim(); Assert-NativeSuccess 'Image version lookup'
    $imageRepoDigestsJson = (& docker image inspect $imageId --format '{{ json .RepoDigests }}').Trim(); Assert-NativeSuccess 'Image digest lookup'
    $imageRepoDigests = if ([string]::IsNullOrWhiteSpace($imageRepoDigestsJson) -or $imageRepoDigestsJson -eq 'null') { @() } else { @($imageRepoDigestsJson | ConvertFrom-Json) }
    Assert-ReleaseBuildMatch -ExpectedCommit $commit -HealthCommit ([string]$health.build.commit) -ImageCommit $imageCommit
    if ([string]$health.build.version -ne $Version -or $imageVersion -ne $Version) { throw "Candidate version mismatch. Expected=$Version Health=$($health.build.version) Image=$imageVersion." }
    $runtimeBuild = [ordered]@{
        version = [string]$health.build.version
        commit = [string]$health.build.commit
        builtAtUtc = ConvertTo-ReleaseUtcTimestamp -Value ([string]$health.build.builtAtUtc)
        channel = [string]$health.build.channel
    }

    $after = Get-StudioFingerprint -Url $BaseUrl
    if ($before.Hash -ne $after.Hash) { throw "Production data fingerprint changed across the Framewright cutover. Before=$($before.Hash) After=$($after.Hash)." }

    $diagnosticsPath = Join-Path $artifactRoot 'framewright-qa-diagnostics.zip'
    Invoke-WebRequest -Uri "$($BaseUrl.AbsoluteUri.TrimEnd('/'))/api/maintenance/diagnostics" -OutFile $diagnosticsPath -TimeoutSec 120
    $integrationResponse = Invoke-RestMethod -Uri "$($BaseUrl.AbsoluteUri.TrimEnd('/'))/api/integrations" -TimeoutSec 30
    $adapterResponse = Invoke-RestMethod -Uri "$($BaseUrl.AbsoluteUri.TrimEnd('/'))/api/generation/adapters" -TimeoutSec 30
    $workflowResponse = Invoke-RestMethod -Uri "$($BaseUrl.AbsoluteUri.TrimEnd('/'))/api/workflows" -TimeoutSec 30
    $integrations = @($integrationResponse)
    $adapters = @($adapterResponse)
    $workflows = @($workflowResponse)
    $voice = Invoke-RestMethod -Uri "$($BaseUrl.AbsoluteUri.TrimEnd('/'))/api/voice/synthesis" -TimeoutSec 15
    $qaBlockers = @()
    if (-not $comfyQueueVerifiedEmpty) { $qaBlockers += "ComfyUI queue readiness could not be inspected before cutover: $comfyQueueUnavailable" }
    foreach ($requiredId in @('comfyui', 'codex')) {
        $integration = $integrations | Where-Object id -eq $requiredId | Select-Object -First 1
        if (-not $integration -or -not $integration.canSubmit) { $qaBlockers += "$requiredId is not ready to submit core QA work." }
    }
    foreach ($requiredAdapter in @('comfyui-fast-draft', 'codex-imagegen', 'comfyui-h3-video')) {
        $adapter = $adapters | Where-Object id -eq $requiredAdapter | Select-Object -First 1
        if (-not $adapter -or -not $adapter.canDispatch) { $qaBlockers += "$requiredAdapter is not ready for mandatory QA." }
    }
    $h3Workflow = $workflows | Where-Object { $_.valid -and $_.capabilities -contains 'last-frame-optional' } | Select-Object -First 1
    if (-not $h3Workflow) { $qaBlockers += 'No validated H3 first/optional-last-frame workflow is available.' }
    if (-not $voice.localCanSynthesize) { $qaBlockers += 'The verified local voice path is not ready for core QA.' }

    $candidate = [ordered]@{
        schemaVersion = 1
        product = 'Framewright'
        version = $Version
        channel = $Channel
        commit = $commit
        upstream = $upstream
        preparedAt = [DateTimeOffset]::UtcNow.ToString('O')
        runtime = [ordered]@{ url = $BaseUrl.AbsoluteUri.TrimEnd('/'); readiness = $ready.status; build = $runtimeBuild }
        image = [ordered]@{ id = $imageId; repoDigests = $imageRepoDigests; version = $imageVersion; revision = $imageCommit }
        data = [ordered]@{ fingerprint = $after.Hash; counts = $after.Counts; preservedAcrossCutover = $true }
        backup = [ordered]@{ file = [IO.Path]::GetFileName($backupPath); sha256 = $backupEvidence.Sha256; entries = $backupEvidence.Entries; manifestSchema = $backupEvidence.ManifestSchema }
        diagnostics = [ordered]@{ file = [IO.Path]::GetFileName($diagnosticsPath); sha256 = Get-ReleaseSha256 -Path $diagnosticsPath }
        tests = [ordered]@{
            completeAutomatedGate = if ($SkipAutomatedVerification) { 'Skipped by operator' } else { 'Passed' }
            windowsPackageSmoke = if ($SkipWindowsPackage) { 'Skipped by operator' } else { 'Passed' }
            installerRollbackSmoke = if ($SkipWindowsPackage) { 'Skipped by operator' } else { 'Passed' }
        }
        qaBlockers = $qaBlockers
        policy = [ordered]@{ providerJobsSubmitted = $false; comfyUiQueueVerifiedEmpty = $comfyQueueVerifiedEmpty; comfyUiQueueModified = $false; gitTagCreated = $false }
    }
    $manifestPath = Join-Path $artifactRoot 'candidate.json'
    [IO.File]::WriteAllText($manifestPath, ($candidate | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    $manifestHash = Get-ReleaseSha256 -Path $manifestPath
    [IO.File]::WriteAllText("$manifestPath.sha256", "$manifestHash  candidate.json`n", [Text.UTF8Encoding]::new($false))

    Write-Host "Framewright $Version candidate prepared at $artifactRoot"
    Write-Host "Commit: $commit · Image: $imageId · Readiness: $($ready.status)"
    if ($qaBlockers.Count -gt 0) { Write-Warning ("Manual QA preflight has blockers: " + ($qaBlockers -join ' ')) }
    else { Write-Host 'Core providers report ready for the manual QA pass. The butler has polished the evidence ledger.' }
}
finally {
    Pop-Location
    if ($null -eq $priorVersion) { Remove-Item Env:FRAMEWRIGHT_VERSION -ErrorAction SilentlyContinue } else { $env:FRAMEWRIGHT_VERSION = $priorVersion }
    if ($null -eq $priorChannel) { Remove-Item Env:FRAMEWRIGHT_CHANNEL -ErrorAction SilentlyContinue } else { $env:FRAMEWRIGHT_CHANNEL = $priorChannel }
}

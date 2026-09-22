[CmdletBinding()]
param(
    [string]$ArtifactPath,
    [int]$Port = 5281
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($ArtifactPath)) { $ArtifactPath = Join-Path $repoRoot 'artifacts\framewright-win-x64' }
$artifact = [IO.Path]::GetFullPath($ArtifactPath)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$artifactsPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $artifact.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "ArtifactPath must stay inside $artifactsRoot" }
$executable = Join-Path $artifact 'Framewright.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Packaged executable missing: $executable" }

$forbidden = Get-ChildItem -LiteralPath $artifact -Recurse -File | Where-Object {
    $_.FullName -match '[\/](App_Data|models|checkpoints|loras)[\/]' -or
    $_.Extension -in '.safetensors', '.ckpt', '.gguf', '.wav', '.mp4'
}
if ($forbidden) { throw "Forbidden package content: $($forbidden.FullName -join ', ')" }
$requiredRuntimeTools = @(
    'scripts\setup-voice-worker.ps1',
    'scripts\start-voice-worker.ps1',
    'scripts\start-installed.ps1',
    'scripts\restore-backup.ps1',
    'tools\voice\qwen_voice_tool.py',
    'tools\voice\qwen_voice_worker.py',
    'tools\voice\prefetch_qwen_voice_models.py',
    'tools\voice\verify_qwen_voice_runtime.py',
    'tools\voice\requirements.lock.txt',
    'tools\voice\models.lock.json'
)
foreach ($relativePath in $requiredRuntimeTools) {
    if (-not (Test-Path -LiteralPath (Join-Path $artifact $relativePath) -PathType Leaf)) {
        throw "Packaged runtime support content is missing: $relativePath"
    }
}
$sourceWorkflowRoot = Join-Path $repoRoot 'workflows'
$sourceLibraryPath = Join-Path $sourceWorkflowRoot 'library.json'
$artifactWorkflowRoot = Join-Path $artifact 'workflows'
$artifactLibraryPath = Join-Path $artifactWorkflowRoot 'library.json'
if (-not (Test-Path -LiteralPath $artifactLibraryPath -PathType Leaf)) { throw 'Packaged workflow library.json is missing.' }
if ((Get-FileHash -LiteralPath $sourceLibraryPath -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $artifactLibraryPath -Algorithm SHA256).Hash) {
    throw 'Packaged workflow library.json does not match the reviewed source manifest.'
}

$workflowLibrary = Get-Content -LiteralPath $sourceLibraryPath -Raw | ConvertFrom-Json
$declaredWorkflows = @($workflowLibrary.workflows | ForEach-Object { [string]$_.file })
if ($declaredWorkflows.Count -eq 0) { throw 'Workflow library.json did not declare any packaged workflows.' }
$duplicateDeclarations = @($declaredWorkflows | Group-Object | Where-Object Count -gt 1)
if ($duplicateDeclarations) { throw "Workflow library.json declares duplicate files: $($duplicateDeclarations.Name -join ', ')" }
foreach ($workflowFile in $declaredWorkflows) {
    if ([string]::IsNullOrWhiteSpace($workflowFile) -or
        [IO.Path]::GetFileName($workflowFile) -ne $workflowFile -or
        [IO.Path]::GetExtension($workflowFile) -ne '.json') {
        throw "Workflow library.json contains an unsafe file declaration: $workflowFile"
    }
    if (-not (Test-Path -LiteralPath (Join-Path $sourceWorkflowRoot $workflowFile) -PathType Leaf)) {
        throw "Workflow library.json points to a missing source file: $workflowFile"
    }
}

$allowedWorkflowFiles = @('library.json', 'README.md') + $declaredWorkflows
$workflowRootPrefix = $artifactWorkflowRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$packagedWorkflowFiles = @(Get-ChildItem -LiteralPath $artifactWorkflowRoot -File -Recurse -ErrorAction Stop | ForEach-Object {
    ([IO.Path]::GetFullPath($_.FullName)).Substring($workflowRootPrefix.Length).Replace([IO.Path]::DirectorySeparatorChar, '/')
})
$missingWorkflows = @($allowedWorkflowFiles | Where-Object { $_ -notin $packagedWorkflowFiles })
if ($missingWorkflows) { throw "Packaged workflow content is missing: $($missingWorkflows -join ', ')" }
$unexpectedWorkflows = @($packagedWorkflowFiles | Where-Object { $_ -notin $allowedWorkflowFiles })
if ($unexpectedWorkflows) { throw "Unexpected packaged workflow content: $($unexpectedWorkflows -join ', ')" }

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$tempRoot = Join-Path $tempBase ("framewright-packaged-smoke-{0}" -f [Guid]::NewGuid().ToString('N'))
$resolvedTemp = [IO.Path]::GetFullPath($tempRoot)
if (-not $resolvedTemp.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Split-Path -Leaf $resolvedTemp).StartsWith('framewright-packaged-smoke-', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe smoke data path: $resolvedTemp"
}

New-Item -ItemType Directory -Path $resolvedTemp | Out-Null
$dataRoot = Join-Path $resolvedTemp 'data'
$voiceRuntime = Join-Path $resolvedTemp 'voice-runtime'
$baseUrl = "http://127.0.0.1:$Port"
$launcher = Join-Path $artifact 'scripts\start-installed.ps1'
$restoreScript = Join-Path $artifact 'scripts\restore-backup.ps1'
$launcherStatePath = Join-Path $voiceRuntime 'state\framewright.pid'
$workingPackagePath = Join-Path $resolvedTemp 'working-package.zip'
$backupPath = Join-Path $resolvedTemp 'framewright-backup.zip'
$restoredAssetPath = Join-Path $resolvedTemp 'restored-package-smoke.png'

function Wait-PackagedReady {
    $health = $null
    foreach ($attempt in 1..40) {
        try {
            $health = Invoke-RestMethod -Uri "$baseUrl/health/ready" -TimeoutSec 1
            if ($health.status -in @('Ready', 'Degraded')) { return $health }
        }
        catch { Start-Sleep -Milliseconds 250 }
    }
    throw 'Packaged binary did not become ready for local work.'
}

function Start-PackagedRuntime {
    $null = & $launcher -Port $Port -VoiceRuntimeRoot $voiceRuntime -SkipVoiceUserEnvironmentUpdate -SkipBrowser
    if (-not (Test-Path -LiteralPath $launcherStatePath -PathType Leaf)) {
        throw 'Managed installed launcher did not record the API process.'
    }
    $launcherState = Get-Content -LiteralPath $launcherStatePath -Raw | ConvertFrom-Json
    return Get-Process -Id ([int]$launcherState.pid) -ErrorAction Stop
}

function Stop-PackagedRuntime([Diagnostics.Process]$Running) {
    if ($Running -and -not $Running.HasExited) {
        Stop-Process -Id $Running.Id -Force
        if (-not $Running.WaitForExit(5000)) { throw 'Packaged binary did not stop within five seconds.' }
    }
    if ($Running) { $Running.Dispose() }
}

function Invoke-ImageUpload([string]$FileName, [string]$Base64) {
    $client = New-Object Net.Http.HttpClient
    $multipart = New-Object Net.Http.MultipartFormDataContent
    $imageContent = $null
    $response = $null
    try {
        $client.DefaultRequestHeaders.Add('X-Storyboard-Studio', '1')
        $pngBytes = [Convert]::FromBase64String($Base64)
        $imageContent = New-Object Net.Http.ByteArrayContent -ArgumentList @(,$pngBytes)
        $imageContent.Headers.ContentType = New-Object Net.Http.Headers.MediaTypeHeaderValue('image/png')
        $multipart.Add($imageContent, 'file', $FileName)
        $response = $client.PostAsync("$baseUrl/api/assets/images", $multipart).GetAwaiter().GetResult()
        $payload = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "Packaged asset write failed with HTTP $([int]$response.StatusCode): $payload"
        }
        return $payload | ConvertFrom-Json
    }
    finally {
        if ($response) { $response.Dispose() }
        if ($imageContent) { $imageContent.Dispose() }
        $multipart.Dispose()
        $client.Dispose()
    }
}

function Import-WorkingPackage([string]$PackagePath) {
    $client = New-Object Net.Http.HttpClient
    $multipart = New-Object Net.Http.MultipartFormDataContent
    $stream = $null
    $fileContent = $null
    $response = $null
    try {
        $stream = [IO.File]::OpenRead($PackagePath)
        $fileContent = New-Object Net.Http.StreamContent -ArgumentList $stream
        $fileContent.Headers.ContentType = New-Object Net.Http.Headers.MediaTypeHeaderValue('application/zip')
        $multipart.Add($fileContent, 'file', 'package-smoke.zip')
        $response = $client.PostAsync("$baseUrl/api/projects/import", $multipart).GetAwaiter().GetResult()
        $payload = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "Packaged project import failed with HTTP $([int]$response.StatusCode): $payload"
        }
        return $payload | ConvertFrom-Json
    }
    finally {
        if ($response) { $response.Dispose() }
        if ($fileContent) { $fileContent.Dispose() }
        elseif ($stream) { $stream.Dispose() }
        $multipart.Dispose()
        $client.Dispose()
    }
}

$oldUrls = $env:Urls
$oldData = $env:Studio__DataRoot
$env:Urls = "http://127.0.0.1:$Port"
$env:Studio__DataRoot = $dataRoot
$process = $null
try {
    Add-Type -AssemblyName System.Net.Http
    $process = Start-PackagedRuntime
    $firstPid = $process.Id
    $health = Wait-PackagedReady
    $snapshot = Invoke-RestMethod -Uri "$baseUrl/api/studio" -TimeoutSec 3
    # Prove the packaged process can perform an actual bounded write into the
    # configured data root. A read-only GET does not initialize asset storage
    # and therefore cannot establish package filesystem readiness.
    $uploaded = Invoke-ImageUpload 'package-smoke-before-backup.png' 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg=='
    if (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'assets\.staging') -PathType Container)) {
        throw 'Packaged DataRoot did not carry the default asset root into the disposable workspace.'
    }

    # Exercise the installed executable's editable export/import path. The
    # imported project must have fresh identities while retaining the asset's
    # immutable content hash.
    Invoke-WebRequest -Uri "$baseUrl/api/export/working-package" -OutFile $workingPackagePath -TimeoutSec 30
    $imported = Import-WorkingPackage $workingPackagePath
    if (-not $imported.idsRemapped -or [int]$imported.assetCount -lt 1) {
        throw 'Packaged project import did not report fresh identities and an imported asset.'
    }
    $null = Invoke-RestMethod -Uri "$baseUrl/api/projects/$($imported.projectId)/activate" -Method Post -TimeoutSec 10
    $importedSnapshot = Invoke-RestMethod -Uri "$baseUrl/api/studio" -TimeoutSec 10
    if ([string]$importedSnapshot.project.id -ne [string]$imported.projectId) {
        throw 'Packaged project import could not be activated.'
    }
    $importedAssetResponse = Invoke-RestMethod -Uri "$baseUrl/api/assets" -TimeoutSec 10
    $importedAssets = @($importedAssetResponse)
    $importedAsset = @($importedAssets | Where-Object contentHash -eq $uploaded.contentHash)
    if ($importedAsset.Count -ne 1) {
        throw 'The packaged project round trip did not preserve the smoke asset content hash.'
    }

    # Back up that imported project, add a different asset after the snapshot,
    # restore offline, and prove the installed binary reopens the exact backup.
    Invoke-WebRequest -Uri "$baseUrl/api/maintenance/backup" -OutFile $backupPath -TimeoutSec 30
    $postBackup = Invoke-ImageUpload 'package-smoke-after-backup.png' 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=='
    if ($postBackup.contentHash -eq $uploaded.contentHash) { throw 'The post-backup marker did not have distinct content.' }

    Stop-PackagedRuntime $process
    $process = $null
    & $restoreScript -BackupPath $backupPath -DataRoot $dataRoot -BaseUrl $baseUrl -Confirm:$false

    $process = Start-PackagedRuntime
    $secondPid = $process.Id
    $health = Wait-PackagedReady
    if ($secondPid -eq $firstPid) { throw 'The packaged restart reused the original process unexpectedly.' }
    $restoredSnapshot = Invoke-RestMethod -Uri "$baseUrl/api/studio" -TimeoutSec 10
    if ([string]$restoredSnapshot.project.id -ne [string]$imported.projectId) {
        throw 'Backup restore did not reopen the imported project that was active at snapshot time.'
    }
    $restoredAssetResponse = Invoke-RestMethod -Uri "$baseUrl/api/assets" -TimeoutSec 10
    $restoredAssets = @($restoredAssetResponse)
    $restoredAsset = @($restoredAssets | Where-Object contentHash -eq $uploaded.contentHash)
    if ($restoredAsset.Count -ne 1) { throw 'Backup restore did not preserve the pre-snapshot asset.' }
    if (@($restoredAssets | Where-Object contentHash -eq $postBackup.contentHash).Count -ne 0) {
        throw 'Backup restore retained an asset created after the snapshot.'
    }
    Invoke-WebRequest -Uri "$baseUrl$($restoredAsset[0].contentUrl)" -OutFile $restoredAssetPath -TimeoutSec 10
    $restoredHash = (Get-FileHash -LiteralPath $restoredAssetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($restoredHash -ne [string]$uploaded.contentHash) {
        throw 'Backup restore returned asset bytes that do not match the recorded content hash.'
    }
    $maintenance = Invoke-RestMethod -Uri "$baseUrl/api/maintenance/status" -TimeoutSec 10
    if ($maintenance.databaseIntegrity -ne 'ok') { throw "Restored database integrity failed: $($maintenance.databaseIntegrity)" }
    if (Test-Path -LiteralPath (Join-Path $artifact 'App_Data')) {
        throw 'Packaged runtime wrote data into the immutable application directory.'
    }
    Write-Host "Packaged readiness: $($health.status)"
    Write-Host "Seeded shots: $($snapshot.shots.Count)"
    Write-Host "Initial project: $($snapshot.project.name)"
    Write-Host "Round-trip project: $($restoredSnapshot.project.name)"
    Write-Host "Restored assets: $($restoredAssets.Count)"
    Write-Host "Restored content hash: $restoredHash"
    Write-Host "Packaged restart: process $firstPid -> $secondPid"
    Write-Host "Package files: $((Get-ChildItem -LiteralPath $artifact -Recurse -File).Count)"
}
finally {
    if ($process) { Stop-PackagedRuntime $process }
    if ($null -eq $oldUrls) { Remove-Item Env:Urls -ErrorAction SilentlyContinue } else { $env:Urls = $oldUrls }
    if ($null -eq $oldData) { Remove-Item Env:Studio__DataRoot -ErrorAction SilentlyContinue } else { $env:Studio__DataRoot = $oldData }
    if ($resolvedTemp.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemp).StartsWith('framewright-packaged-smoke-', [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTemp)) {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}

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
$requiredVoiceBootstrap = @(
    'scripts\setup-voice-worker.ps1',
    'scripts\start-voice-worker.ps1',
    'scripts\start-installed.ps1',
    'tools\voice\qwen_voice_tool.py',
    'tools\voice\qwen_voice_worker.py',
    'tools\voice\prefetch_qwen_voice_models.py',
    'tools\voice\verify_qwen_voice_runtime.py',
    'tools\voice\requirements.lock.txt',
    'tools\voice\models.lock.json'
)
foreach ($relativePath in $requiredVoiceBootstrap) {
    if (-not (Test-Path -LiteralPath (Join-Path $artifact $relativePath) -PathType Leaf)) {
        throw "Packaged local-voice bootstrap content is missing: $relativePath"
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
$oldUrls = $env:Urls
$oldData = $env:Studio__DataRoot
$env:Urls = "http://127.0.0.1:$Port"
$env:Studio__DataRoot = $resolvedTemp
$process = $null
try {
    $launcher = Join-Path $artifact 'scripts\start-installed.ps1'
    $voiceRuntime = Join-Path $resolvedTemp 'voice-runtime'
    & $launcher -Port $Port -VoiceRuntimeRoot $voiceRuntime -SkipVoiceUserEnvironmentUpdate -SkipBrowser
    $launcherStatePath = Join-Path $voiceRuntime 'state\framewright.pid'
    if (-not (Test-Path -LiteralPath $launcherStatePath -PathType Leaf)) { throw 'Managed installed launcher did not record the API process.' }
    $launcherState = Get-Content -LiteralPath $launcherStatePath -Raw | ConvertFrom-Json
    $process = Get-Process -Id ([int]$launcherState.pid) -ErrorAction Stop
    $ready = $false
    foreach ($attempt in 1..40) {
        if ($ready) { break }
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health/ready" -TimeoutSec 1
            $ready = $health.status -in @('Ready', 'Degraded')
        }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw 'Packaged binary did not become ready for local work.' }
    $snapshot = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/studio" -TimeoutSec 3
    $assets = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/assets" -TimeoutSec 3
    # Prove the packaged process can perform an actual bounded write into the
    # configured data root. A read-only GET does not initialize asset storage
    # and therefore cannot establish package filesystem readiness.
    Add-Type -AssemblyName System.Net.Http
    $client = New-Object Net.Http.HttpClient
    $multipart = New-Object Net.Http.MultipartFormDataContent
    $imageContent = $null
    $uploadResponse = $null
    try {
        $client.DefaultRequestHeaders.Add('X-Storyboard-Studio', '1')
        $pngBytes = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9YKPk4sAAAAASUVORK5CYII=')
        $imageContent = New-Object Net.Http.ByteArrayContent -ArgumentList @(,$pngBytes)
        $imageContent.Headers.ContentType = New-Object Net.Http.Headers.MediaTypeHeaderValue('image/png')
        $multipart.Add($imageContent, 'file', 'package-smoke.png')
        $uploadResponse = $client.PostAsync("http://127.0.0.1:$Port/api/assets/images", $multipart).GetAwaiter().GetResult()
        if (-not $uploadResponse.IsSuccessStatusCode) {
            $detail = $uploadResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            throw "Packaged asset write failed with HTTP $([int]$uploadResponse.StatusCode): $detail"
        }
    }
    finally {
        if ($uploadResponse) { $uploadResponse.Dispose() }
        if ($imageContent) { $imageContent.Dispose() }
        $multipart.Dispose()
        $client.Dispose()
    }
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedTemp 'assets\.staging') -PathType Container)) {
        throw 'Packaged DataRoot did not carry the default asset root into the disposable workspace.'
    }
    if (Test-Path -LiteralPath (Join-Path $artifact 'App_Data')) {
        throw 'Packaged runtime wrote data into the immutable application directory.'
    }
    Write-Host "Packaged readiness: $($health.status)"
    Write-Host "Seeded shots: $($snapshot.shots.Count)"
    Write-Host "Project: $($snapshot.project.name)"
    Write-Host "Isolated assets: $($assets.Count)"
    Write-Host "Package files: $((Get-ChildItem -LiteralPath $artifact -Recurse -File).Count)"
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        $null = $process.WaitForExit(5000)
    }
    if ($process) { $process.Dispose() }
    if ($null -eq $oldUrls) { Remove-Item Env:Urls -ErrorAction SilentlyContinue } else { $env:Urls = $oldUrls }
    if ($null -eq $oldData) { Remove-Item Env:Studio__DataRoot -ErrorAction SilentlyContinue } else { $env:Studio__DataRoot = $oldData }
    if ($resolvedTemp.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemp).StartsWith('framewright-packaged-smoke-', [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTemp)) {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}

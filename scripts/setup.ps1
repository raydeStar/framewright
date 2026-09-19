[CmdletBinding()]
param(
    [ValidateSet('Docker', 'Native')]
    [string]$Mode = 'Docker',
    [string]$ComfyUiEndpoint = 'http://127.0.0.1:8188',
    [switch]$AllowRemoteComfyUi,
    [switch]$EnableCodexImageGen,
    [switch]$EnableComfyStills,
    [switch]$EnableComfyVideo,
    [switch]$EnableYuE2,
    [string]$YuE2Endpoint = 'http://127.0.0.1:5182',
    [string]$YuE2Model = 'm-a-p/YuE2-3B',
    [string]$YuE2Vae = 'm-a-p/YuE2-Vae',
    [string]$YuE2Device = 'cuda',
    [string]$YuE2WorkerToken,
    [switch]$EnableLocalVoice,
    [switch]$Launch,
    [string]$EnvironmentPath,
    [string]$LocalSettingsPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$environmentFile = if ([string]::IsNullOrWhiteSpace($EnvironmentPath)) { Join-Path $repoRoot '.env' } else { [IO.Path]::GetFullPath($EnvironmentPath) }
$settingsFile = if ([string]::IsNullOrWhiteSpace($LocalSettingsPath)) { Join-Path $repoRoot 'appsettings.Local.json' } else { [IO.Path]::GetFullPath($LocalSettingsPath) }
$comfyRequested = $EnableComfyStills -or $EnableComfyVideo

function Test-LoopbackUri([Uri]$Uri) {
    return $Uri.IsLoopback -or $Uri.Host -eq 'localhost' -or $Uri.Host -eq 'host.docker.internal'
}

function Get-ComfyInspectionUri([Uri]$Uri) {
    if ($Uri.Host -ne 'host.docker.internal') { return $Uri }
    $builder = [UriBuilder]::new($Uri)
    $builder.Host = '127.0.0.1'
    return $builder.Uri
}

function Get-ComfyObjectInfo([Uri]$Endpoint) {
    $probeEndpoint = Get-ComfyInspectionUri $Endpoint
    $base = $probeEndpoint.AbsoluteUri.TrimEnd('/')
    try {
        $queue = Invoke-RestMethod -Method Get -Uri "$base/queue" -TimeoutSec 5
        $objectInfo = Invoke-RestMethod -Method Get -Uri "$base/object_info" -TimeoutSec 30
    }
    catch {
        throw "ComfyUI is not reachable for read-only validation at $base. Nothing was enabled. $($_.Exception.Message)"
    }
    $running = @($queue.queue_running).Count
    $pending = @($queue.queue_pending).Count
    Write-Host "ComfyUI answered read-only checks: $running running, $pending pending. The queue was not changed."
    return $objectInfo
}

function Get-InputChoices($NodeDefinition, [string]$InputName) {
    foreach ($kind in @('required', 'optional')) {
        $group = $NodeDefinition.input.$kind
        if ($null -eq $group) { continue }
        $property = $group.PSObject.Properties[$InputName]
        if ($null -eq $property -or $null -eq $property.Value) { continue }
        $definition = @($property.Value)
        if ($definition.Count -eq 0) { continue }
        return @($definition[0])
    }
    return @()
}

function Assert-ComfyRequirements($ObjectInfo) {
    $libraryPath = Join-Path $repoRoot 'workflows\library.json'
    $library = Get-Content -LiteralPath $libraryPath -Raw | ConvertFrom-Json
    $selectedIds = New-Object System.Collections.Generic.List[string]
    if ($EnableComfyStills) {
        foreach ($id in @('text-draft', 'reference-draft', 'fast-draft', 'current-frame-edit')) { $selectedIds.Add($id) }
    }
    if ($EnableComfyVideo) { $selectedIds.Add('h3-video-i2v') }

    $missingNodes = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::Ordinal)
    $missingModels = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)
    foreach ($workflowId in $selectedIds) {
        $workflow = @($library.workflows | Where-Object { $_.id -eq $workflowId })
        if ($workflow.Count -ne 1) { throw "Packaged workflow '$workflowId' is missing or duplicated in workflows/library.json." }
        foreach ($nodeType in @($workflow[0].requiredNodeTypes)) {
            if ($null -eq $ObjectInfo.PSObject.Properties[$nodeType]) { [void]$missingNodes.Add("$workflowId -> $nodeType") }
        }
        foreach ($modelProperty in @($workflow[0].requiredModels.PSObject.Properties)) {
            $parts = $modelProperty.Name.Split('.', 2)
            if ($parts.Count -ne 2) { throw "Workflow '$workflowId' has an invalid requiredModels key '$($modelProperty.Name)'." }
            $nodeDefinition = $ObjectInfo.PSObject.Properties[$parts[0]].Value
            if ($null -eq $nodeDefinition) { continue }
            $choices = @(Get-InputChoices $nodeDefinition $parts[1] | ForEach-Object { ([string]$_).Replace('/', '\') })
            $expected = ([string]$modelProperty.Value).Replace('/', '\')
            if (-not ($choices -contains $expected)) { [void]$missingModels.Add("$workflowId -> $expected") }
        }
    }
    if ($missingNodes.Count -gt 0 -or $missingModels.Count -gt 0) {
        $details = New-Object System.Collections.Generic.List[string]
        if ($missingNodes.Count -gt 0) { $details.Add("missing nodes: $([string]::Join(', ', @($missingNodes)))") }
        if ($missingModels.Count -gt 0) { $details.Add("missing models: $([string]::Join(', ', @($missingModels)))") }
        throw "Requested ComfyUI lanes were not enabled; $([string]::Join('; ', @($details))). The ward seals the door before the dragon reaches the queue."
    }
    Write-Host "Validated $($selectedIds.Count) packaged workflow contract(s) against ComfyUI's installed nodes and model choices."
}

function Set-EnvironmentValues([string]$Path, [Collections.IDictionary]$Values) {
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $lines = if (Test-Path -LiteralPath $Path -PathType Leaf) { @([IO.File]::ReadAllLines($Path)) } else { @() }
    foreach ($entry in $Values.GetEnumerator()) {
        $pattern = "^$([regex]::Escape([string]$entry.Key))="
        $lines = @($lines | Where-Object { $_ -notmatch $pattern }) + "$($entry.Key)=$($entry.Value)"
    }
    [IO.File]::WriteAllLines($Path, $lines, [Text.UTF8Encoding]::new($false))
}

function Get-OrAddObjectProperty($Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property -and $null -ne $property.Value) { return $property.Value }
    $value = [pscustomobject]@{}
    if ($null -eq $property) { $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $value }
    else { $property.Value = $value }
    return $value
}

function Set-ObjectProperty($Object, [string]$Name, $Value) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value }
    else { $property.Value = $Value }
}

function New-SecureWorkerToken {
    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) }
    finally { $generator.Dispose() }
    return -join ($bytes | ForEach-Object { $_.ToString('x2') })
}

$endpointUri = $null
if (-not [Uri]::TryCreate($ComfyUiEndpoint, [UriKind]::Absolute, [ref]$endpointUri) -or $endpointUri.Scheme -notin @('http', 'https')) {
    throw 'ComfyUiEndpoint must be an absolute HTTP or HTTPS URL.'
}
if ($comfyRequested -and -not (Test-LoopbackUri $endpointUri) -and -not $AllowRemoteComfyUi) {
    throw 'Remote ComfyUI requires -AllowRemoteComfyUi. Nothing was written.'
}
if ($comfyRequested) {
    $objectInfo = Get-ComfyObjectInfo $endpointUri
    Assert-ComfyRequirements $objectInfo
}

$yue2EndpointUri = $null
if (-not [Uri]::TryCreate($YuE2Endpoint, [UriKind]::Absolute, [ref]$yue2EndpointUri) -or $yue2EndpointUri.Scheme -notin @('http', 'https')) {
    throw 'YuE2Endpoint must be an absolute HTTP or HTTPS URL.'
}
if ($EnableYuE2 -and -not (Test-LoopbackUri $yue2EndpointUri)) {
    throw 'YuE2 setup is local-first. Use loopback or host.docker.internal; remote inference requires a manual security review.'
}
$generatedYuE2Token = $false
if ($EnableYuE2 -and $Mode -eq 'Docker' -and [string]::IsNullOrWhiteSpace($YuE2WorkerToken)) {
    if (Test-Path -LiteralPath $environmentFile -PathType Leaf) {
        $existingTokenEntry = [IO.File]::ReadAllLines($environmentFile) |
            Where-Object { $_ -match '^FRAMEWRIGHT_YUE2_WORKER_TOKEN=(.+)$' } |
            Select-Object -Last 1
        if ($existingTokenEntry) { $YuE2WorkerToken = $existingTokenEntry.Split('=', 2)[1] }
    }
    if ([string]::IsNullOrWhiteSpace($YuE2WorkerToken)) {
        $YuE2WorkerToken = New-SecureWorkerToken
        $generatedYuE2Token = $true
        Write-Host 'Generated a private YuE2 worker token for Docker-to-host access. Its value is written only to the ignored local environment.'
    }
}
if ($EnableYuE2) {
    $probe = Get-ComfyInspectionUri $yue2EndpointUri
    $headers = @{}
    if (-not [string]::IsNullOrWhiteSpace($YuE2WorkerToken)) { $headers['X-Framewright-Worker-Token'] = $YuE2WorkerToken }
    try {
        $health = Invoke-RestMethod -Method Get -Uri "$($probe.AbsoluteUri.TrimEnd('/'))/health" -Headers $headers -TimeoutSec 5
    }
    catch {
        throw "YuE2 is not reachable for read-only validation at $($probe.AbsoluteUri.TrimEnd('/')). Nothing was enabled. $($_.Exception.Message)"
    }
    if (-not $health.ready) { throw "YuE2 answered but is not ready: $($health.detail). Nothing was enabled." }
    if ($Mode -eq 'Docker' -and (([int]$health.queued -gt 0) -or ([int]$health.running -gt 0))) {
        throw 'YuE2 has queued or running work. Docker setup will not restart it until the queue is idle; nothing was written.'
    }
    Write-Host "YuE2 answered a read-only health check: model '$($health.model)' on '$($health.device)'. No composition or render was submitted."
}

if ($Mode -eq 'Docker') {
    $dockerEndpoint = $endpointUri.AbsoluteUri.TrimEnd('/')
    if ($endpointUri.IsLoopback -or $endpointUri.Host -eq 'localhost') {
        $builder = [UriBuilder]::new($endpointUri)
        $builder.Host = 'host.docker.internal'
        $dockerEndpoint = $builder.Uri.AbsoluteUri.TrimEnd('/')
    }
    $dockerYuE2Endpoint = $yue2EndpointUri.AbsoluteUri.TrimEnd('/')
    if ($yue2EndpointUri.IsLoopback -or $yue2EndpointUri.Host -eq 'localhost') {
        $yueBuilder = [UriBuilder]::new($yue2EndpointUri)
        $yueBuilder.Host = 'host.docker.internal'
        $dockerYuE2Endpoint = $yueBuilder.Uri.AbsoluteUri.TrimEnd('/')
    }
    Set-EnvironmentValues $environmentFile ([ordered]@{
        FRAMEWRIGHT_COMFYUI_ENDPOINT = $dockerEndpoint
        FRAMEWRIGHT_COMFYUI_ALLOW_REMOTE = $(if ($comfyRequested) { 'true' } else { 'false' })
        FRAMEWRIGHT_COMFYUI_STILLS_ENABLED = $EnableComfyStills.ToString().ToLowerInvariant()
        FRAMEWRIGHT_COMFYUI_VIDEO_ENABLED = $EnableComfyVideo.ToString().ToLowerInvariant()
        FRAMEWRIGHT_YUE2_ENABLED = $EnableYuE2.ToString().ToLowerInvariant()
        FRAMEWRIGHT_YUE2_ENDPOINT = $dockerYuE2Endpoint
        FRAMEWRIGHT_YUE2_WORKER_TOKEN = $YuE2WorkerToken
        FRAMEWRIGHT_YUE2_MODEL = $YuE2Model
        FRAMEWRIGHT_YUE2_VAE = $YuE2Vae
        FRAMEWRIGHT_YUE2_DEVICE = $YuE2Device
        FRAMEWRIGHT_CODEX_IMAGEGEN_ENABLED = $EnableCodexImageGen.ToString().ToLowerInvariant()
        FRAMEWRIGHT_LOCAL_VOICE_ENABLED = $EnableLocalVoice.ToString().ToLowerInvariant()
    })
    Write-Host "Wrote Docker setup to $environmentFile (no credential values were requested)."
}
else {
    $settings = if (Test-Path -LiteralPath $settingsFile -PathType Leaf) { Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    $integrations = Get-OrAddObjectProperty $settings 'Integrations'
    $comfy = Get-OrAddObjectProperty $integrations 'ComfyUi'
    $codex = Get-OrAddObjectProperty $integrations 'Codex'
    $qwen = Get-OrAddObjectProperty $settings 'QwenTts'
    $yue2 = Get-OrAddObjectProperty $settings 'YuE2'
    Set-ObjectProperty $comfy 'Endpoint' $endpointUri.AbsoluteUri.TrimEnd('/')
    Set-ObjectProperty $comfy 'AllowRemote' ([bool]$AllowRemoteComfyUi)
    Set-ObjectProperty $comfy 'SubmissionEnabled' ([bool]$EnableComfyStills)
    Set-ObjectProperty $comfy 'VideoSubmissionEnabled' ([bool]$EnableComfyVideo)
    Set-ObjectProperty $yue2 'Enabled' ([bool]$EnableYuE2)
    Set-ObjectProperty $yue2 'Endpoint' $yue2EndpointUri.AbsoluteUri.TrimEnd('/')
    Set-ObjectProperty $yue2 'WorkerToken' $YuE2WorkerToken
    Set-ObjectProperty $yue2 'Model' $YuE2Model
    Set-ObjectProperty $yue2 'Vae' $YuE2Vae
    Set-ObjectProperty $yue2 'Device' $YuE2Device
    Set-ObjectProperty $codex 'NonInteractiveImageEnabled' ([bool]$EnableCodexImageGen)
    Set-ObjectProperty $qwen 'Enabled' ([bool]$EnableLocalVoice)
    $parent = Split-Path -Parent $settingsFile
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    [IO.File]::WriteAllText($settingsFile, (($settings | ConvertTo-Json -Depth 12) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    Write-Host "Wrote native setup to $settingsFile (no credential values were requested)."
}

if ($EnableYuE2 -and $Mode -eq 'Docker') {
    & (Join-Path $repoRoot 'scripts\start-yue2-worker.ps1') -Restart
    $tokenReceipt = if ($generatedYuE2Token) { ' with the generated private token' } else { '' }
    Write-Host "Restarted the idle YuE2 worker$tokenReceipt so Docker can reach the authenticated host service. No generation job was submitted."
}

$enabled = @()
if ($EnableCodexImageGen) { $enabled += 'Codex ImageGen' }
if ($EnableComfyStills) { $enabled += 'ComfyUI stills' }
if ($EnableComfyVideo) { $enabled += 'H3 video' }
if ($EnableYuE2) { $enabled += 'YuE2 composition and rendering' }
if ($EnableLocalVoice) { $enabled += 'local Qwen voice' }
$laneSummary = if ($enabled.Count -eq 0) { 'All optional provider and voice lanes remain off.' } else { "Enabled selected lanes: $([string]::Join(', ', $enabled))." }
Write-Host "$laneSummary A quiet ward is still a ward."

if ($Launch) {
    & (Join-Path $repoRoot 'scripts\start.ps1') -Native:($Mode -eq 'Native') -OpenSetup
    exit $LASTEXITCODE
}

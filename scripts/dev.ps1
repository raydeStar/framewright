[CmdletBinding()]
param([switch]$OpenSetup, [switch]$NoBrowser)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$webRoot = Join-Path $repoRoot 'src\storyboard-studio-web'
$apiProject = Join-Path $repoRoot 'src\StoryboardStudio.Api\StoryboardStudio.Api.csproj'

function Assert-NativeSuccess([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step failed with exit code $LASTEXITCODE. The butler refuses to serve yesterday's bundle." }
}

Push-Location $webRoot
try {
    npm ci
    Assert-NativeSuccess 'npm ci'
    npm run build
    Assert-NativeSuccess 'frontend build'
}
finally {
    Pop-Location
}

if ([string]::IsNullOrWhiteSpace($env:ASPNETCORE_ENVIRONMENT)) { $env:ASPNETCORE_ENVIRONMENT = 'Local' }
$url = if ($OpenSetup) { 'http://127.0.0.1:5179/?setup=1' } else { 'http://127.0.0.1:5179' }
Write-Host "Framewright is starting at $url"
Write-Host 'Press Ctrl+C to stop it. ComfyUI discovery is read-only by default.'
if (-not $NoBrowser) { Start-Process $url }
dotnet run --project $apiProject --no-launch-profile

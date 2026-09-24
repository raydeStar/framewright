[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$OutputPath,
    [string]$Version = 'development',
    [string]$Commit = 'unknown',
    [string]$BuiltAtUtc = 'unknown',
    [string]$Channel = 'development'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$webRoot = Join-Path $repoRoot 'src\storyboard-studio-web'
$apiProject = Join-Path $repoRoot 'src\StoryboardStudio.Api\StoryboardStudio.Api.csproj'
$launcherProject = Join-Path $repoRoot 'src\Framewright.Launcher\Framewright.Launcher.csproj'
function Assert-NativeSuccess([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step failed with exit code $LASTEXITCODE." }
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot ("artifacts\framewright-{0}" -f $Runtime)
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$artifactsPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

if ($resolvedOutput -eq $artifactsRoot -or -not $resolvedOutput.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must be a dedicated folder inside the repository artifacts directory: $resolvedOutput"
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

if (Test-Path -LiteralPath $resolvedOutput) {
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}
# The projects declare every supported release RID, so one checked-in lock
# contains the Linux container and Windows package graphs. Supplying -r here
# would replace that set with one RID and make locked mode correctly refuse it.
dotnet restore $apiProject --locked-mode
Assert-NativeSuccess 'locked publish restore'
dotnet publish $apiProject -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false --no-restore -o $resolvedOutput `
    -p:FramewrightVersion=$Version -p:FramewrightCommit=$Commit -p:FramewrightBuiltAtUtc=$BuiltAtUtc -p:FramewrightChannel=$Channel
Assert-NativeSuccess 'dotnet publish'

# The double-clickable launcher, beside Framewright.exe: a trimmed single file
# that starts the studio hidden and opens the browser, with no PowerShell.
dotnet restore $launcherProject --locked-mode
Assert-NativeSuccess 'locked launcher restore'
dotnet publish $launcherProject -c Release -r $Runtime --no-restore -o $resolvedOutput `
    -p:FramewrightVersion=$Version -p:FramewrightCommit=$Commit -p:FramewrightBuiltAtUtc=$BuiltAtUtc -p:FramewrightChannel=$Channel
Assert-NativeSuccess 'launcher publish'

if (Test-Path -LiteralPath (Join-Path $resolvedOutput 'App_Data')) {
    throw 'Publish unexpectedly included App_Data. Refusing to produce a package that could contain artist data.'
}
if (Test-Path -LiteralPath (Join-Path $resolvedOutput 'appsettings.Local.json')) {
    throw 'Publish included this workstation''s appsettings.Local.json. Refusing to package local configuration.'
}
foreach ($required in @('Framewright.exe', 'Framewright Studio.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedOutput $required))) {
        throw "Publish did not produce $required."
    }
}

# What this package is, for the portable zip and for anyone holding a copy.
$versionInfo = [ordered]@{ product = 'Framewright'; version = $Version; commit = $Commit; builtAtUtc = $BuiltAtUtc; channel = $Channel; runtime = $Runtime }
[IO.File]::WriteAllText((Join-Path $resolvedOutput 'version.json'), ($versionInfo | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

Write-Host "Published Framewright to $resolvedOutput"
Write-Host 'No ComfyUI source, custom nodes, models, credentials, or production assets were included. Framewright-owned workflows and the pinned external voice-runtime bootstrap were packaged.'

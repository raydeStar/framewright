[CmdletBinding()]
param(
    [string]$ArtifactPath,
    [switch]$SkipSmoke
)

<#
  Turns a published package (scripts\publish-local.ps1) into the portable zip an
  artist downloads: unzip, double-click "Framewright Studio.exe". The zip holds
  one "Framewright" folder, so a newer zip extracted over an older one replaces
  the application and leaves the App_Data folder, the artist's work, alone.

  Unless -SkipSmoke is given, the zip is extracted to a disposable folder and
  scripts\smoke-launcher.ps1 is run against the extracted copy, so what is
  proven is the download itself, not the folder it was made from.
#>
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($ArtifactPath)) { $ArtifactPath = Join-Path $repoRoot 'artifacts\framewright-win-x64' }
$package = [IO.Path]::GetFullPath($ArtifactPath)
$versionPath = Join-Path $package 'version.json'
if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) { throw "No version.json in $package. Publish with scripts\publish-local.ps1 first." }
$info = Get-Content -LiteralPath $versionPath -Raw | ConvertFrom-Json
foreach ($required in @('Framewright.exe', 'Framewright Studio.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $required) -PathType Leaf)) { throw "The package is missing $required." }
}
# Artist data, workstation configuration and keys never travel in a download.
$forbidden = @(Get-ChildItem -LiteralPath $package -Recurse -Force | Where-Object {
    $_.Name -in @('App_Data', '.env', 'appsettings.Local.json', 'generation-settings.json', 'auth.json') -or $_.Extension -in @('.pfx', '.key', '.db', '.sqlite')
})
if ($forbidden.Count -gt 0) { throw "Refusing to zip a package that contains $(($forbidden | ForEach-Object FullName) -join ', ')." }

$name = "framewright-$($info.version)-$($info.runtime)"
$zipPath = Join-Path $repoRoot "artifacts\$name.zip"
$staging = Join-Path $repoRoot "artifacts\zip-staging-$([Guid]::NewGuid().ToString('N'))"
$smokeRoot = $null
try {
    $folder = Join-Path $staging 'Framewright'
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Copy-Item -Path (Join-Path $package '*') -Destination $folder -Recurse -Force
    [IO.File]::WriteAllText((Join-Path $folder 'Stop Framewright.cmd'),
        "@echo off`r`nrem Stops the Framewright studio started from this folder, and nothing else.`r`n`"%~dp0Framewright Studio.exe`" --stop`r`n",
        [Text.Encoding]::ASCII)
    $readme = @"
Framewright $($info.version)
Built from $($info.commit) on $($info.builtAtUtc) ($($info.channel)).

START
  Double-click "Framewright Studio.exe". The studio opens in your browser at
  http://127.0.0.1:5179 and is reachable only from this computer.

  This build is not code-signed, so the first time Windows may say "Windows
  protected your PC". Choose "More info", then "Run anyway".

STOP
  Double-click "Stop Framewright.cmd". Closing the browser tab leaves the
  studio running, so work in progress is never cut off.

YOUR WORK
  Projects, images and video are kept in the App_Data folder inside this
  folder. Keep this folder together. Production setup > Safety copies and
  tablet access makes a backup you can restore anywhere.

UPDATING
  Stop Framewright, then extract the newer zip over this folder and choose
  "Replace". A download never contains App_Data, so your work stays.

MAKING IMAGES AND VIDEO
  Everything except generation works straight away, including a sample
  project to explore. To generate, open Production setup > Image and video
  generation and connect ComfyUI or Codex. Nothing is sent anywhere until you
  turn it on there.

  Video export and review use FFmpeg and FFprobe on your PATH. Local voice and
  music need their own workers: see https://github.com/raydeStar/framewright.

If the studio does not start, a message explains why; the full log is in
%LOCALAPPDATA%\FramewrightVoice\state\framewright.stderr.log.
"@
    [IO.File]::WriteAllText((Join-Path $folder 'READ ME FIRST.txt'), ($readme -replace "`r?`n", "`r`n"), [Text.UTF8Encoding]::new($false))

    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($staging, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$zipPath.sha256", "$hash  $name.zip`n", [Text.UTF8Encoding]::new($false))

    if (-not $SkipSmoke) {
        $smokeRoot = Join-Path ([IO.Path]::GetTempPath()) "framewright-zip-smoke-$([Guid]::NewGuid().ToString('N'))"
        [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $smokeRoot)
        foreach ($expected in @('Framewright.exe', 'Framewright Studio.exe', 'Stop Framewright.cmd', 'READ ME FIRST.txt', 'version.json')) {
            if (-not (Test-Path -LiteralPath (Join-Path $smokeRoot "Framewright\$expected") -PathType Leaf)) { throw "The zip is missing Framewright\$expected." }
        }
        & $env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'smoke-launcher.ps1') -ArtifactPath (Join-Path $smokeRoot 'Framewright')
        if ($LASTEXITCODE -ne 0) { throw "The extracted zip failed the launcher smoke ($LASTEXITCODE)." }
    }
    $size = '{0:N0} MB' -f ((Get-Item -LiteralPath $zipPath).Length / 1MB)
    Write-Host "Portable zip: $zipPath ($size, sha256 $hash)"
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
    if ($smokeRoot -and (Split-Path -Leaf $smokeRoot).StartsWith('framewright-zip-smoke-') -and (Test-Path -LiteralPath $smokeRoot)) {
        Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

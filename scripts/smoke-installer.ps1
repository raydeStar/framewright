[CmdletBinding()]
param(
    [string]$ArtifactPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($ArtifactPath)) { $ArtifactPath = Join-Path $repoRoot 'artifacts\framewright-win-x64' }
$artifact = [IO.Path]::GetFullPath($ArtifactPath)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$artifactsPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $artifact.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "ArtifactPath must stay inside $artifactsRoot" }
if (-not (Test-Path -LiteralPath (Join-Path $artifact 'Framewright.exe') -PathType Leaf)) { throw "Packaged executable missing in $artifact" }

$nonce = [Guid]::NewGuid().ToString('N')
$localAppData = [IO.Path]::GetFullPath($env:LOCALAPPDATA)
$smokeInstallBase = [IO.Path]::GetFullPath((Join-Path $localAppData 'Framewright Installer Smoke'))
$smokeInstallPrefix = $smokeInstallBase.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$installRoot = [IO.Path]::GetFullPath((Join-Path $smokeInstallBase $nonce))
$voiceRoot = [IO.Path]::GetFullPath((Join-Path $smokeInstallBase "$nonce-voice"))
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$tempPrefix = $tempBase.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$fakeAppData = [IO.Path]::GetFullPath((Join-Path $tempBase "framewright-installer-appdata-$nonce"))
if (-not $installRoot.StartsWith($smokeInstallPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe installer smoke root: $installRoot" }
if (-not $fakeAppData.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or -not (Split-Path -Leaf $fakeAppData).StartsWith('framewright-installer-appdata-', [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe installer APPDATA root: $fakeAppData" }

$oldAppData = $env:APPDATA
try {
    New-Item -ItemType Directory -Path (Join-Path $installRoot 'App_Data') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $installRoot 'wwwroot\assets') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $fakeAppData 'Microsoft\Windows\Start Menu\Programs') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $installRoot 'App_Data\preserve.contract'), 'artist-data')
    [IO.File]::WriteAllText((Join-Path $installRoot 'stale.contract'), 'stale')
    [IO.File]::WriteAllText((Join-Path $installRoot 'wwwroot\assets\stale.contract'), 'stale')
    $legacyVoiceToken = 'installer-migration-token-0123456789abcdef0123456789abcdef'
    [IO.File]::WriteAllText((Join-Path $installRoot '.env'), "FRAMEWRIGHT_VOICE_WORKER_TOKEN=$legacyVoiceToken`n", [Text.UTF8Encoding]::new($false))
    $env:APPDATA = $fakeAppData

    & (Join-Path $PSScriptRoot 'install-local.ps1') -PublishPath $artifact -InstallRoot $installRoot -VoiceRuntimeRoot $voiceRoot

    if (-not (Test-Path -LiteralPath (Join-Path $installRoot 'App_Data\preserve.contract') -PathType Leaf)) { throw 'Installer removed durable App_Data.' }
    if (Test-Path -LiteralPath (Join-Path $installRoot 'stale.contract')) { throw 'Installer retained a stale root file.' }
    if (Test-Path -LiteralPath (Join-Path $installRoot 'wwwroot\assets\stale.contract')) { throw 'Installer retained a stale nested file.' }
    if (-not (Test-Path -LiteralPath (Join-Path $installRoot 'Framewright.exe') -PathType Leaf)) { throw 'Installer omitted the executable.' }
    $durableTokenPath = Join-Path $voiceRoot 'state\voice-worker.token'
    if (-not (Test-Path -LiteralPath $durableTokenPath -PathType Leaf) -or [IO.File]::ReadAllText($durableTokenPath) -ne $legacyVoiceToken) {
        throw 'Installer did not migrate the legacy voice token outside the replaceable package root.'
    }
    $shortcutPath = Join-Path $fakeAppData 'Microsoft\Windows\Start Menu\Programs\Framewright.lnk'
    if (-not (Test-Path -LiteralPath $shortcutPath -PathType Leaf)) { throw 'Installer omitted the shortcut.' }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    if ($shortcut.TargetPath -notmatch 'powershell\.exe$' -or $shortcut.Arguments -notmatch 'start-installed\.ps1') {
        throw 'Installed shortcut does not use the managed voice/API/browser launcher.'
    }

    # Prove a failure after the staged package becomes active restores the exact
    # previous install and artist data instead of leaving a mixed generation.
    $rollbackMarker = Join-Path $installRoot 'rollback.contract'
    [IO.File]::WriteAllText($rollbackMarker, 'previous-package-generation', [Text.UTF8Encoding]::new($false))
    $beforeExecutable = (Get-FileHash -LiteralPath (Join-Path $installRoot 'Framewright.exe') -Algorithm SHA256).Hash
    $injected = $false
    try {
        & (Join-Path $PSScriptRoot 'install-local.ps1') -PublishPath $artifact -InstallRoot $installRoot `
            -VoiceRuntimeRoot $voiceRoot -TestFailurePoint AfterActivation
    }
    catch { $injected = $_.Exception.Message -match 'Injected installer failure' }
    if (-not $injected) { throw 'Installer failure injection did not execute.' }
    if (-not (Test-Path -LiteralPath $rollbackMarker -PathType Leaf) -or [IO.File]::ReadAllText($rollbackMarker) -ne 'previous-package-generation') {
        throw 'Installer rollback did not restore the exact previous package inventory.'
    }
    if ((Get-FileHash -LiteralPath (Join-Path $installRoot 'Framewright.exe') -Algorithm SHA256).Hash -ne $beforeExecutable -or
        -not (Test-Path -LiteralPath (Join-Path $installRoot 'App_Data\preserve.contract') -PathType Leaf)) {
        throw 'Installer rollback changed the prior executable or durable artist data.'
    }
    Write-Host 'Installer smoke passed: atomic package swap/rollback, durable data, and managed shortcut are verified.'
}
finally {
    if ($null -eq $oldAppData) { Remove-Item Env:APPDATA -ErrorAction SilentlyContinue } else { $env:APPDATA = $oldAppData }
    if ($installRoot.StartsWith($smokeInstallPrefix, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $installRoot)) {
        Remove-Item -LiteralPath $installRoot -Recurse -Force
    }
    if ($voiceRoot.StartsWith($smokeInstallPrefix, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $voiceRoot)) {
        Remove-Item -LiteralPath $voiceRoot -Recurse -Force
    }
    if ((Test-Path -LiteralPath $smokeInstallBase -PathType Container) -and -not (Get-ChildItem -LiteralPath $smokeInstallBase -Force)) {
        Remove-Item -LiteralPath $smokeInstallBase -Force
    }
    if ($fakeAppData.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $fakeAppData).StartsWith('framewright-installer-appdata-', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $fakeAppData)) {
        Remove-Item -LiteralPath $fakeAppData -Recurse -Force
    }
}

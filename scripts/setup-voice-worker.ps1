[CmdletBinding()]
param(
    [string]$PythonPath = 'C:\Comfy\python_embeded\python.exe',
    [string]$RuntimeRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$durableLocalAppData = [IO.Path]::GetFullPath((Join-Path $env:USERPROFILE 'AppData\Local'))
if ([string]::IsNullOrWhiteSpace($RuntimeRoot)) {
    # Packaged desktop hosts may virtualize LOCALAPPDATA into their own package
    # cache. Voice models belong to the workstation user, not to Codex's app
    # container, so derive the durable Windows profile path explicitly.
    $RuntimeRoot = Join-Path $durableLocalAppData 'FramewrightVoice'
}
$requirements = Join-Path $repoRoot 'tools\voice\requirements.lock.txt'
$modelLock = Join-Path $repoRoot 'tools\voice\models.lock.json'
$prefetcher = Join-Path $repoRoot 'tools\voice\prefetch_qwen_voice_models.py'
$verifier = Join-Path $repoRoot 'tools\voice\verify_qwen_voice_runtime.py'
$bridge = Join-Path $repoRoot 'tools\voice\qwen_voice_tool.py'
$resolvedRuntime = [IO.Path]::GetFullPath($RuntimeRoot)
$localAppData = $durableLocalAppData
$localAppDataPrefix = $localAppData.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($resolvedRuntime -eq $localAppData -or -not $resolvedRuntime.StartsWith($localAppDataPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "RuntimeRoot must be a dedicated directory inside LocalAppData: $resolvedRuntime"
}
$packageRoot = Join-Path $resolvedRuntime 'packages'
$manifest = Join-Path $resolvedRuntime 'runtime-manifest.json'
$canary = Join-Path $resolvedRuntime 'canary\voice-setup.wav'

if (-not (Test-Path -LiteralPath $PythonPath -PathType Leaf)) {
    throw @"
The GPU Python runtime was not found at $PythonPath.
Install ComfyUI first or rerun this command with -PythonPath pointing to its
embedded python.exe. Framewright will continue to run without local voice.
"@
}
foreach ($requiredPath in @($requirements, $modelLock, $prefetcher, $verifier, $bridge)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "The checked-in voice setup file is missing: $requiredPath"
    }
}

function Find-SoxExecutable {
    $command = Get-Command sox.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'sox-14-4-2\sox.exe'),
        (Join-Path $env:ProgramFiles 'sox-14-4-2\sox.exe')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    if ($candidates.Count -gt 0) { return $candidates[0] }

    $wingetRoot = Join-Path $durableLocalAppData 'Microsoft\WinGet\Packages'
    if (Test-Path -LiteralPath $wingetRoot -PathType Container) {
        $wingetSox = Get-ChildItem -LiteralPath $wingetRoot -Directory -Filter 'ChrisBagwell.SoX_*' -ErrorAction SilentlyContinue |
            ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -File -Filter 'sox.exe' -Recurse -ErrorAction SilentlyContinue } |
            Select-Object -First 1
        if ($wingetSox) { return $wingetSox.FullName }
    }
    return $null
}

$soxPath = Find-SoxExecutable
if (-not $soxPath) {
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw 'SoX 14.4.2 is required for Qwen audio processing. Install ChrisBagwell.SoX with winget, then rerun setup.'
    }
    Write-Host 'Installing the pinned SoX 14.4.2 audio runtime with winget.'
    & $winget.Source install --exact --id ChrisBagwell.SoX --version 14.4.2 --silent --accept-package-agreements --accept-source-agreements
    $installExitCode = $LASTEXITCODE
    $soxPath = Find-SoxExecutable
    if ($soxPath) {
        $soxDirectory = Split-Path -Parent $soxPath
        $env:Path = "$soxDirectory;$env:Path"
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if ($null -eq $userPath) { $userPath = '' }
        if (($userPath -split ';') -notcontains $soxDirectory) {
            [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ";$soxDirectory").TrimStart(';'), 'User')
        }
    }
    if (-not $soxPath) { throw "SoX installation failed with exit code $installExitCode and its executable could not be located." }
}
$soxDirectory = Split-Path -Parent $soxPath
if (($env:Path -split ';') -notcontains $soxDirectory) {
    # WinGet may have installed SoX during an earlier shell. Make it visible to
    # the canary and every child process in this setup run immediately.
    $env:Path = "$soxDirectory;$env:Path"
}
& $soxPath --version | Select-Object -First 1 | Write-Host

$packagePrefix = $resolvedRuntime.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$resolvedPackages = [IO.Path]::GetFullPath($packageRoot)
if (-not $resolvedPackages.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Leaf $resolvedPackages) -ne 'packages') {
    throw "Refusing to replace an unexpected package directory: $resolvedPackages"
}
# A target install can retain files from older wheels. Rebuild this dedicated
# package boundary exactly so the canary manifest inventories one coherent set.
if (Test-Path -LiteralPath $resolvedPackages -PathType Container) {
    Remove-Item -LiteralPath $resolvedPackages -Recurse -Force
}
New-Item -ItemType Directory -Path $resolvedPackages -Force | Out-Null
Write-Host "Installing Framewright's pinned voice packages into $packageRoot"
& $PythonPath -m pip install `
    --disable-pip-version-check `
    --no-input `
    --no-deps `
    --upgrade `
    --target $packageRoot `
    --requirement $requirements
if ($LASTEXITCODE -ne 0) {
    throw "Voice dependency installation failed with exit code $LASTEXITCODE. The oracle refuses to bluff."
}

Write-Host 'Downloading the two exact Qwen model revisions and recording their SHA-256 inventory. This is roughly 9 GB and may take a while.'
& $PythonPath $prefetcher --lock $modelLock --runtime-root $resolvedRuntime --manifest $manifest
if ($LASTEXITCODE -ne 0) {
    throw "Pinned voice model setup failed with exit code $LASTEXITCODE."
}

Write-Host 'Running one real, offline synthesis canary. Model loading may take several minutes.'
& $PythonPath $verifier --packages $packageRoot --manifest $manifest --lock $modelLock --canary-output $canary
if ($LASTEXITCODE -ne 0) {
    throw "Voice runtime verification failed with exit code $LASTEXITCODE."
}

[Environment]::SetEnvironmentVariable('FRAMEWRIGHT_VOICE_RUNTIME_ROOT', $resolvedRuntime, 'User')
[Environment]::SetEnvironmentVariable('QwenTts__PythonPath', [IO.Path]::GetFullPath($PythonPath), 'User')
[Environment]::SetEnvironmentVariable('QwenTts__PackagePath', $packageRoot, 'User')
[Environment]::SetEnvironmentVariable('QwenTts__ManifestPath', $manifest, 'User')
[Environment]::SetEnvironmentVariable('QwenTts__ScriptPath', [IO.Path]::GetFullPath($bridge), 'User')
Write-Host 'Local voice setup is complete. Restart Framewright from its installed shortcut.'
Write-Host 'When working from a source checkout, start it with .\scripts\start.ps1.'

[CmdletBinding()]
param(
    [string]$PythonPath = 'py',
    [string]$RuntimeRoot,
    [switch]$InstallRuntime,
    [string]$YuERevision,
    [switch]$AllowModelDownloads
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$runtime = if ([string]::IsNullOrWhiteSpace($RuntimeRoot)) { Join-Path $repoRoot '.yue2-runtime' } else { [IO.Path]::GetFullPath($RuntimeRoot) }
$python = Get-Command $PythonPath -ErrorAction Stop
$venvPython = Join-Path $runtime 'Scripts\python.exe'

if ($InstallRuntime -and [string]::IsNullOrWhiteSpace($YuERevision)) {
    throw '-YuERevision is required with -InstallRuntime. Pin an audited official YuE commit instead of installing a moving branch.'
}
if (-not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
    Write-Host "Creating a dedicated YuE2 Python environment at $runtime. No model weights are downloaded by this step."
    if ($python.Name -eq 'py.exe') { & $python.Source -3.12 -m venv $runtime }
    else { & $python.Source -m venv $runtime }
    if ($LASTEXITCODE -ne 0) { throw "Python could not create the YuE2 environment (exit $LASTEXITCODE). The orchestra has misplaced its pit." }
}

if ($InstallRuntime) {
    $source = "git+https://github.com/multimodal-art-projection/YuE.git@$YuERevision"
    Write-Host "Installing the official YuE repository at pinned revision $YuERevision into the isolated environment."
    & $venvPython -m pip install --upgrade pip
    if ($LASTEXITCODE -ne 0) { throw 'pip could not update in the YuE2 environment.' }
    & $venvPython -m pip install $source
    if ($LASTEXITCODE -ne 0) { throw "YuE2 installation failed (exit $LASTEXITCODE). No Framewright settings were changed." }
}

$probe = & $venvPython -c "import importlib.util; print('ready' if importlib.util.find_spec('yue2') else 'missing')"
if ($LASTEXITCODE -ne 0 -or $probe -notcontains 'ready') {
    throw "The environment exists but the 'yue2' package is missing. Re-run with -InstallRuntime -YuERevision <audited-commit>."
}

$manifestPath = Join-Path $runtime 'framewright-runtime.json'
$recordedRevision = $YuERevision
if ([string]::IsNullOrWhiteSpace($recordedRevision) -and (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    $existingManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $recordedRevision = [string]$existingManifest.revision
}
$manifest = [ordered]@{
    schemaVersion = 1
    python = $venvPython
    officialRepository = 'https://github.com/multimodal-art-projection/YuE'
    revision = $recordedRevision
    modelDownloadsAllowed = [bool]$AllowModelDownloads
    configuredAt = [DateTimeOffset]::UtcNow.ToString('O')
}
[IO.File]::WriteAllText($manifestPath, (($manifest | ConvertTo-Json -Depth 4) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
Write-Host "YuE2 runtime is ready at $runtime. Model downloads allowed: $([bool]$AllowModelDownloads). No song was generated."

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$webRoot = Join-Path $repoRoot 'src\storyboard-studio-web'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testData = Join-Path $tempRoot ("storyboard-studio-e2e-{0}" -f [Guid]::NewGuid().ToString('N'))
$resolvedTestData = [IO.Path]::GetFullPath($testData)

if (-not $resolvedTestData.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Split-Path -Leaf $resolvedTestData).StartsWith('storyboard-studio-e2e-', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use an unexpected E2E data path: $resolvedTestData"
}

New-Item -ItemType Directory -Path $resolvedTestData | Out-Null

# wwwroot is intentionally ignored and absent from a clean checkout. Build the
# exact browser bundle this server will host so Playwright can never pass
# against a stale artifact left by a developer's earlier run.
Push-Location $webRoot
try {
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "E2E frontend build failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }

$env:Urls = 'http://127.0.0.1:5180'
$env:Studio__DataRoot = $resolvedTestData
$env:Integrations__ComfyUi__Endpoint = 'http://127.0.0.1:1'
$env:Integrations__ComfyUi__SubmissionEnabled = 'false'
$env:Integrations__ComfyUi__VideoSubmissionEnabled = 'false'
$env:YuE2__Enabled = 'false'
$env:Integrations__OpenAI__SubmissionEnabled = 'false'

$stopFile = $env:STUDIO_E2E_STOP_FILE
if ([string]::IsNullOrWhiteSpace($stopFile)) {
    throw 'STUDIO_E2E_STOP_FILE is required so the test server can shut down cleanly.'
}

$resolvedStopFile = [IO.Path]::GetFullPath($stopFile)
if (-not $resolvedStopFile.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Split-Path -Leaf $resolvedStopFile).StartsWith('storyboard-studio-e2e-', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use an unexpected E2E stop path: $resolvedStopFile"
}

if (Test-Path -LiteralPath $resolvedStopFile) {
    Remove-Item -LiteralPath $resolvedStopFile -Force
}

$apiProject = Join-Path $repoRoot 'src\StoryboardStudio.Api\StoryboardStudio.Api.csproj'
$apiRoot = Split-Path -Parent $apiProject

# Build in this process, then launch the resulting DLL as an owned child. On
# Windows, Playwright cannot reliably terminate a PowerShell/dotnet process
# tree in restricted environments; the sentinel lets this script stop its own
# child and return before Playwright performs its fallback teardown.
dotnet build $apiProject --no-restore --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "E2E API build failed with exit code $LASTEXITCODE." }

$apiDll = Join-Path $apiRoot 'bin\Debug\net10.0\Framewright.dll'
if (-not (Test-Path -LiteralPath $apiDll)) {
    throw "E2E API output was not found: $apiDll"
}

$startParameters = @{
    FilePath = 'dotnet'
    ArgumentList = @("`"$apiDll`"")
    WorkingDirectory = $apiRoot
    PassThru = $true
}
if ($env:OS -eq 'Windows_NT') {
    $startParameters['WindowStyle'] = 'Hidden'
}

$apiProcess = $null
$stopRequested = $false
try {
    $apiProcess = Start-Process @startParameters
    while (-not $apiProcess.HasExited) {
        if (Test-Path -LiteralPath $resolvedStopFile) {
            $stopRequested = $true
            Stop-Process -Id $apiProcess.Id -Force
            $apiProcess.WaitForExit()
            break
        }

        Start-Sleep -Milliseconds 100
    }

    if (-not $stopRequested -and $apiProcess.ExitCode -ne 0) {
        throw "E2E API exited early with code $($apiProcess.ExitCode)."
    }
}
finally {
    if ($null -ne $apiProcess -and -not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id -Force
        $apiProcess.WaitForExit()
    }

    if (Test-Path -LiteralPath $resolvedTestData) {
        Remove-Item -LiteralPath $resolvedTestData -Recurse -Force
    }

    if (Test-Path -LiteralPath $resolvedStopFile) {
        Remove-Item -LiteralPath $resolvedStopFile -Force
    }
}

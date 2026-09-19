$ErrorActionPreference = 'Stop'

if ($env:STUDIO_ALLOW_REAL_COMFYUI_JOURNEY -ne 'I_UNDERSTAND_THIS_SUBMITS_REAL_JOBS') {
    throw 'Real provider journey is locked. Set STUDIO_ALLOW_REAL_COMFYUI_JOURNEY=I_UNDERSTAND_THIS_SUBMITS_REAL_JOBS only for an intentional GPU run.'
}

# Server for the production journey test.
#
# Unlike run-e2e-server.ps1 — which deliberately points at a dead ComfyUI port
# with submission disabled so the normal suite can never touch a GPU or a real
# queue — this one talks to the REAL ComfyUI and really renders.
#
# It still uses a throwaway data root, so a journey run never writes into the
# artist's project database or asset store. Only the GPU is shared, and the
# adapters join the existing queue rather than disturbing it.

$repoRoot = Split-Path -Parent $PSScriptRoot
$webRoot = Join-Path $repoRoot 'src\storyboard-studio-web'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testData = Join-Path $tempRoot ("storyboard-studio-journey-{0}" -f [Guid]::NewGuid().ToString('N'))
$resolvedTestData = [IO.Path]::GetFullPath($testData)

if (-not $resolvedTestData.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Split-Path -Leaf $resolvedTestData).StartsWith('storyboard-studio-journey-', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use an unexpected journey data path: $resolvedTestData"
}

New-Item -ItemType Directory -Path $resolvedTestData | Out-Null
Push-Location $webRoot
try {
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "Journey frontend build failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }

$env:Urls = 'http://127.0.0.1:5181'
$env:Studio__DataRoot = $resolvedTestData
$env:Integrations__ComfyUi__Endpoint = 'http://127.0.0.1:8188'
$env:Integrations__ComfyUi__SubmissionEnabled = 'true'
$env:YuE2__Enabled = 'false'
$env:Integrations__ComfyUi__VideoSubmissionEnabled = 'true'

try {
    dotnet run --project (Join-Path $repoRoot 'src\StoryboardStudio.Api\StoryboardStudio.Api.csproj') --no-launch-profile
}
finally {
    if (Test-Path -LiteralPath $resolvedTestData) {
        Remove-Item -LiteralPath $resolvedTestData -Recurse -Force
    }
}

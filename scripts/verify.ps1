$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$webRoot = Join-Path $repoRoot 'src\storyboard-studio-web'

function Assert-NativeSuccess([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step failed with exit code $LASTEXITCODE." }
}

Push-Location $webRoot
try {
    npm ci
    Assert-NativeSuccess 'npm ci'
    npm audit --audit-level=high
    Assert-NativeSuccess 'npm audit'
    npm run check
    Assert-NativeSuccess 'frontend source checks'
    npm run build
    Assert-NativeSuccess 'frontend build'
}
finally {
    Pop-Location
}

Push-Location $repoRoot
try {
    & $env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts\public-release-audit.ps1')
    Assert-NativeSuccess 'public release audit'
    dotnet restore Framewright.slnx --locked-mode
    Assert-NativeSuccess 'dotnet restore'
    dotnet build Framewright.slnx --configuration Release --no-restore -t:Rebuild --warnaserror
    Assert-NativeSuccess 'dotnet build'
    dotnet test Framewright.slnx --configuration Release --no-build --no-restore
    Assert-NativeSuccess 'dotnet test'
    & $env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts\tests\restore-backup.tests.ps1')
    Assert-NativeSuccess 'backup restore snapshot tests'
    & $env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts\tests\start-launcher.tests.ps1')
    Assert-NativeSuccess 'Docker-unavailable launcher fallback test'
    & $env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts\tests\docker-env.tests.ps1')
    Assert-NativeSuccess 'Docker environment merge test'
    & $env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts\tests\setup.tests.ps1')
    Assert-NativeSuccess 'guided setup safety contract test'
    & $env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts\tests\voice-worker-pid.tests.ps1')
    Assert-NativeSuccess 'voice worker PID ownership test'
    & $env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts\tests\release-candidate.tests.ps1')
    Assert-NativeSuccess 'release candidate safety contract test'
    $voicePythonPath = $null
    if (Test-Path -LiteralPath 'C:\Comfy\python_embeded\python.exe' -PathType Leaf) {
        $voicePythonPath = [IO.Path]::GetFullPath('C:\Comfy\python_embeded\python.exe')
    }
    if (-not $voicePythonPath) {
        foreach ($candidateName in @('python.exe', 'python3.exe')) {
            $candidate = Get-Command $candidateName -ErrorAction SilentlyContinue
            if (-not $candidate) { continue }
            $candidatePath = if ($candidate.Path) { $candidate.Path } else { $candidate.Source }
            if ([string]::IsNullOrWhiteSpace($candidatePath)) { continue }
            try {
                & $candidatePath -c 'import sys; assert sys.version_info >= (3, 10)' 2>$null
                if ($LASTEXITCODE -eq 0) { $voicePythonPath = $candidatePath; break }
            }
            catch { }
        }
    }
    if (-not $voicePythonPath) { throw 'Python is required to test the local voice bootstrap contracts.' }
    & $voicePythonPath -m unittest discover -s (Join-Path $repoRoot 'tools\voice\tests') -v
    Assert-NativeSuccess 'local voice bootstrap contract tests'
    & $voicePythonPath -m unittest discover -s (Join-Path $repoRoot 'tools\yue2\tests') -v
    Assert-NativeSuccess 'local YuE2 worker safety contract tests'
    dotnet list Framewright.slnx package --vulnerable --include-transitive
    Assert-NativeSuccess 'NuGet vulnerability inspection'
}
finally {
    Pop-Location
}

Push-Location $webRoot
try {
    npm run test:e2e
    Assert-NativeSuccess 'Playwright end-to-end tests'
}
finally {
    Pop-Location
}

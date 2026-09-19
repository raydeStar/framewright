[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$testBase = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\script-tests'))
New-Item -ItemType Directory -Path $testBase -Force | Out-Null
$testRoot = Join-Path $testBase ("framewright-launcher-test-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$fakeDocker = Join-Path $testRoot 'docker.exe'
$previousPath = $env:Path
$previousWorkerToken = $env:QwenTts__WorkerToken
$previousWorkerEndpoint = $env:QwenTts__WorkerEndpoint
$previousVoiceEnabled = $env:FRAMEWRIGHT_LOCAL_VOICE_ENABLED
try {
    # A renamed Windows utility is a deterministic native executable that
    # rejects Docker's arguments with a non-zero exit code. Unlike Add-Type's
    # ConsoleApplication output, this contract works in Windows PowerShell 5.1
    # and the PowerShell 7 host used by GitHub Actions.
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\find.exe') -Destination $fakeDocker
    $env:Path = $testRoot
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $output = & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts\start.ps1') -ProbeOnly
    if ($LASTEXITCODE -ne 0) { throw "Launcher probe failed with exit code $LASTEXITCODE." }
    if (($output | Select-Object -Last 1) -ne 'Native') { throw "Docker failure did not select the native fallback: $output" }

    # Run the selected path with harmless injected scripts. This proves the
    # automatic fallback attempts the selected optional voice boundary as -Native
    # before starting the app, without launching either real service.
    $fakeScripts = Join-Path $testRoot 'scripts'
    New-Item -ItemType Directory -Path $fakeScripts | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\start.ps1') -Destination $fakeScripts
    $firstRunToken = 'first-run-token-0123456789abcdef0123456789abcdef'
    [IO.File]::WriteAllText((Join-Path $fakeScripts 'start-voice-worker.ps1'), "`$env:QwenTts__WorkerToken='$firstRunToken'; `$env:QwenTts__WorkerEndpoint='http://127.0.0.1:5181'; [IO.File]::WriteAllText(`$env:FRAMEWRIGHT_TEST_VOICE_MARKER, 'voice')", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $fakeScripts 'dev.ps1'), '[IO.File]::WriteAllText($env:FRAMEWRIGHT_TEST_APP_MARKER, "app"); [IO.File]::WriteAllText($env:FRAMEWRIGHT_TEST_TOKEN_MARKER, $env:QwenTts__WorkerToken)', [Text.UTF8Encoding]::new($false))
    $env:FRAMEWRIGHT_TEST_VOICE_MARKER = Join-Path $testRoot 'voice.marker'
    $env:FRAMEWRIGHT_TEST_APP_MARKER = Join-Path $testRoot 'app.marker'
    $env:FRAMEWRIGHT_TEST_TOKEN_MARKER = Join-Path $testRoot 'token.marker'
    Remove-Item Env:QwenTts__WorkerToken -ErrorAction SilentlyContinue
    Remove-Item Env:QwenTts__WorkerEndpoint -ErrorAction SilentlyContinue
    $env:FRAMEWRIGHT_LOCAL_VOICE_ENABLED = 'true'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fakeScripts 'start.ps1') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Native fallback execution failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $env:FRAMEWRIGHT_TEST_VOICE_MARKER) -or -not (Test-Path -LiteralPath $env:FRAMEWRIGHT_TEST_APP_MARKER)) {
        throw 'Automatic native fallback did not start the selected voice worker before the app.'
    }
    if ([IO.File]::ReadAllText($env:FRAMEWRIGHT_TEST_TOKEN_MARKER) -ne $firstRunToken) {
        throw 'The first-run voice token was not available to the API process launched in the same shell.'
    }
    Write-Host 'Launcher fallback test passed: failing Docker selects native mode and carries the first-run voice token into the app.'
}
finally {
    $env:Path = $previousPath
    if ($null -eq $previousWorkerToken) { Remove-Item Env:QwenTts__WorkerToken -ErrorAction SilentlyContinue } else { $env:QwenTts__WorkerToken = $previousWorkerToken }
    if ($null -eq $previousWorkerEndpoint) { Remove-Item Env:QwenTts__WorkerEndpoint -ErrorAction SilentlyContinue } else { $env:QwenTts__WorkerEndpoint = $previousWorkerEndpoint }
    if ($null -eq $previousVoiceEnabled) { Remove-Item Env:FRAMEWRIGHT_LOCAL_VOICE_ENABLED -ErrorAction SilentlyContinue } else { $env:FRAMEWRIGHT_LOCAL_VOICE_ENABLED = $previousVoiceEnabled }
    Remove-Item Env:FRAMEWRIGHT_TEST_VOICE_MARKER -ErrorAction SilentlyContinue
    Remove-Item Env:FRAMEWRIGHT_TEST_APP_MARKER -ErrorAction SilentlyContinue
    Remove-Item Env:FRAMEWRIGHT_TEST_TOKEN_MARKER -ErrorAction SilentlyContinue
    $resolvedTest = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTest.StartsWith($testBase, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTest).StartsWith('framewright-launcher-test-', [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTest)) {
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force
    }
}

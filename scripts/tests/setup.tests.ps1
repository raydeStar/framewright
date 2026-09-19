[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("framewright-setup-test-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $environmentPath = Join-Path $testRoot '.env'
    [IO.File]::WriteAllText($environmentPath, "PRESERVE_ME=yes`n", [Text.UTF8Encoding]::new($false))
    & (Join-Path $repoRoot 'scripts\setup.ps1') -Mode Docker -EnvironmentPath $environmentPath
    $environment = @([IO.File]::ReadAllLines($environmentPath))
    if ($environment -notcontains 'PRESERVE_ME=yes' -or
        $environment -notcontains 'FRAMEWRIGHT_COMFYUI_STILLS_ENABLED=false' -or
        $environment -notcontains 'FRAMEWRIGHT_COMFYUI_VIDEO_ENABLED=false' -or
        $environment -notcontains 'FRAMEWRIGHT_YUE2_ENABLED=false' -or
        $environment -notcontains 'FRAMEWRIGHT_CODEX_IMAGEGEN_ENABLED=false' -or
        $environment -notcontains 'FRAMEWRIGHT_LOCAL_VOICE_ENABLED=false') {
        throw 'Docker setup did not preserve unrelated values and fail closed.'
    }

    $settingsPath = Join-Path $testRoot 'appsettings.Local.json'
    [IO.File]::WriteAllText($settingsPath, '{"Studio":{"AllowLan":false}}', [Text.UTF8Encoding]::new($false))
    & (Join-Path $repoRoot 'scripts\setup.ps1') -Mode Native -LocalSettingsPath $settingsPath
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if ($settings.Studio.AllowLan -ne $false -or
        $settings.Integrations.ComfyUi.SubmissionEnabled -ne $false -or
        $settings.Integrations.ComfyUi.VideoSubmissionEnabled -ne $false -or
        $settings.YuE2.Enabled -ne $false -or
        $settings.Integrations.Codex.NonInteractiveImageEnabled -ne $false -or
        $settings.QwenTts.Enabled -ne $false) {
        throw 'Native setup did not preserve unrelated settings and fail closed.'
    }

    $defaults = Get-Content -LiteralPath (Join-Path $repoRoot 'src\StoryboardStudio.Api\appsettings.json') -Raw | ConvertFrom-Json
    if ($defaults.Integrations.ComfyUi.SubmissionEnabled -ne $false -or
        $defaults.Integrations.Codex.NonInteractiveImageEnabled -ne $false -or
        $defaults.QwenTts.Enabled -ne $false -or
        $defaults.YuE2.Enabled -ne $false) {
        throw 'Tracked application defaults still enable an optional provider or voice lane.'
    }

    $setupSource = Get-Content -LiteralPath (Join-Path $repoRoot 'scripts\setup.ps1') -Raw
    if ($setupSource -notmatch 'RandomNumberGenerator' -or
        $setupSource -notmatch 'FRAMEWRIGHT_YUE2_WORKER_TOKEN' -or
        $setupSource -notmatch 'queued or running work' -or
        $setupSource -notmatch "start-yue2-worker\.ps1'\) -Restart") {
        throw 'Docker YuE2 setup no longer guarantees an authenticated, idle-only worker restart.'
    }
    Write-Host 'Setup contracts passed: local values are preserved, all provider lanes default off, and no provider probe occurs for disabled lanes.'
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}

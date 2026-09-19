[CmdletBinding()]
param([switch]$History)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Push-Location $repoRoot
try {
    if ((& git rev-parse --is-inside-work-tree 2>$null) -ne 'true') { throw 'Run the public release audit from a Git worktree.' }
    $findings = New-Object System.Collections.Generic.List[object]
    # Include untracked, non-ignored files so the pre-commit audit checks the
    # exact candidate tree rather than politely overlooking the newest hazard.
    $tracked = @(& git ls-files --cached --others --exclude-standard)

    function Add-Finding([string]$Scope, [string]$Rule, [string]$Path, [int]$Line = 0) {
        $findings.Add([pscustomobject]@{ Scope = $Scope; Rule = $Rule; Path = $Path; Line = $Line })
    }

    function Test-AllowlistedFixture([string]$Rule, [string]$Path, [string]$Line) {
        $normalized = $Path.Replace('\', '/')
        if ($Rule -eq 'OpenAI-style key' -and $normalized -like 'tests/*' -and $Line -match '(test-only|must-not-cross-boundary)') { return $true }
        if ($Rule -eq 'Assigned secret-like value' -and
            ($normalized -eq '.env.example' -or $normalized -eq '.github/workflows/ci.yml' -or $normalized -eq 'scripts/smoke-installer.ps1' -or $normalized -like 'scripts/tests/*') -and
            $Line -match '(placeholder|replace-with|migration-token|test-token|0123456789abcdef)') { return $true }
        if ($Rule -eq 'User profile path' -and $normalized -eq '.env.example' -and $Line -match 'YourName') { return $true }
        return $false
    }

    $forbiddenPath = '(?i)(^|/)(\.env($|\.)|App_Data|backups|artifacts|\.runtime|\.voice-runtime|\.qwen-voice-runtime|models|checkpoints|loras|vae)(/|$)|(?i)\.(db|sqlite3?|pfx|p12|pem|key|safetensors|ckpt|gguf)$|(?i)(^|/)(auth\.json|credentials?\.json|secrets?\.json)$'
    foreach ($path in $tracked) {
        $normalized = $path.Replace('\', '/')
        $example = $normalized -in @('.env.example', 'appsettings.Local.example.json')
        if (-not $example -and $normalized -match $forbiddenPath) { Add-Finding 'tracked' 'Forbidden release path' $path }
        $item = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
        if ($item -and $item.Length -gt 10MB) { Add-Finding 'tracked' 'Tracked file exceeds 10 MiB' $path }
    }

    $rules = [ordered]@{
        'OpenAI-style key' = 'sk-[A-Za-z0-9_-]{20,}'
        'GitHub token' = 'gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{30,}'
        'AWS access key' = 'AKIA[0-9A-Z]{16}'
        'Google API key' = 'AIza[0-9A-Za-z_-]{30,}'
        'Slack token' = 'xox[baprs]-[A-Za-z0-9-]{10,}'
        'Private key block' = 'BEGIN (RSA |EC |OPENSSH |DSA )?PRIVATE KEY'
        'JWT-like token' = 'eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}'
        'Azure storage key' = 'AccountKey=[A-Za-z0-9+/]{30,}={0,2}'
        'Assigned secret-like value' = '(?i)(password|passwd|secret|token|api[_-]?key)[A-Za-z0-9_.-]*\s*[:=]\s*["'']?[A-Za-z0-9+/_-]{24,}'
        'Email address' = '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}'
        'User profile path' = '(?i)[A-Z]:\\Users\\[A-Za-z0-9._-]+|/Users/[A-Za-z0-9._-]+'
    }

    foreach ($path in $tracked) {
        $item = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
        if (-not $item -or $item.Length -gt 5MB) { continue }
        $bytes = [IO.File]::ReadAllBytes($item.FullName)
        if ($bytes -contains 0) { continue }
        $lines = [IO.File]::ReadAllLines($item.FullName)
        for ($index = 0; $index -lt $lines.Length; $index++) {
            foreach ($entry in $rules.GetEnumerator()) {
                if ($lines[$index] -notmatch $entry.Value) { continue }
                if (-not (Test-AllowlistedFixture $entry.Key $path $lines[$index])) { Add-Finding 'tracked' $entry.Key $path ($index + 1) }
            }
        }
    }

    foreach ($remote in @(& git remote get-url --all origin 2>$null)) {
        if ($remote -match '^https?://[^/@\s]+@') { Add-Finding 'git' 'Remote URL contains user information' 'origin' }
    }

    if ($History) {
        $revisions = @(& git rev-list --all)
        foreach ($entry in $rules.GetEnumerator()) {
            $ere = $entry.Value.Replace('(?i)', '')
            $hits = @(& git grep -I -n -E -- $ere @revisions 2>$null)
            foreach ($hit in $hits) {
                if ($hit -notmatch '^(?<revision>[0-9a-f]+):(?<path>.*?):(?<line>\d+):(?<text>.*)$') { continue }
                if (-not (Test-AllowlistedFixture $entry.Key $Matches.path $Matches.text)) { Add-Finding 'history' $entry.Key $Matches.path ([int]$Matches.line) }
            }
        }
        $personalAuthorCommits = 0
        foreach ($identity in @(& git log --all --format='%H|%ae')) {
            $parts = $identity.Split('|', 2)
            if ($parts.Count -eq 2 -and $parts[1] -notmatch '(?i)(noreply|users\.noreply\.github\.com)$') {
                $personalAuthorCommits++
            }
        }
        if ($personalAuthorCommits -gt 0) {
            Add-Finding 'history' 'Commit author email is not a no-reply address' "$personalAuthorCommits reachable commit(s)"
        }
    }

    $unique = @($findings | Sort-Object Scope, Rule, Path, Line -Unique)
    if ($unique.Count -gt 0) {
        [Console]::Error.WriteLine("Public release audit found $($unique.Count) item(s). Values are deliberately redacted.")
        $unique | Select-Object -First 100 | ForEach-Object {
            $location = if ($_.Line -gt 0) { "$($_.Path):$($_.Line)" } else { $_.Path }
            Write-Host "[$($_.Scope)] $($_.Rule) -> $location"
        }
        exit 1
    }
    $scope = if ($History) { 'candidate files and reachable history' } else { 'tracked and untracked candidate files' }
    Write-Host "Public release audit passed for $scope. No sensitive values were printed; the order is pleased."
}
finally { Pop-Location }

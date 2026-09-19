[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$setupScript = [IO.File]::ReadAllText((Join-Path $repoRoot 'scripts\setup-voice-worker.ps1'))
$startScript = [IO.File]::ReadAllText((Join-Path $repoRoot 'scripts\start-voice-worker.ps1'))
$verifier = [IO.File]::ReadAllText((Join-Path $repoRoot 'tools\voice\verify_qwen_voice_runtime.py'))
if ($setupScript -notmatch "USERPROFILE 'AppData\\Local'" -or $startScript -notmatch "USERPROFILE 'AppData\\Local'") {
    throw 'Voice setup and startup must use the durable user profile instead of a packaged host LOCALAPPDATA cache.'
}
if ($setupScript -notmatch 'ChrisBagwell\.SoX_\*' -or $setupScript -notmatch 'Find-SoxExecutable') {
    throw 'Voice setup must rediscover an existing per-user WinGet SoX installation before trying to install it again.'
}
if ($setupScript -notmatch '\$env:Path = "\$soxDirectory;\$env:Path"') {
    throw 'Voice setup must expose rediscovered SoX to the synthesis canary in the current process.'
}
if ($startScript -notmatch "GetEnvironmentVariable\('Path', 'User'\)") {
    throw 'Voice worker startup must refresh the user PATH before launching Qwen audio helpers.'
}
if ($verifier -notmatch 'Path\(__file__\)\.resolve\(\)\.parent') {
    throw 'The real synthesis canary must make the checked-in worker module importable.'
}
$testBase = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\script-tests'))
New-Item -ItemType Directory -Path $testBase -Force | Out-Null
$testRoot = Join-Path $testBase ("framewright-voice-pid-test-{0}" -f [Guid]::NewGuid().ToString('N'))
$ownedWorkerIds = New-Object System.Collections.Generic.List[int]
New-Item -ItemType Directory -Path $testRoot | Out-Null

try {
    $fakePython = Join-Path $testRoot 'fake-python.exe'
    $source = @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

public static class Program
{
    public static int Main(string[] args)
    {
        int port = 0;
        for (int index = 0; index + 1 < args.Length; index++)
        {
            if (String.Equals(args[index], "--port", StringComparison.Ordinal))
            {
                port = Int32.Parse(args[index + 1]);
            }
        }
        string token = Environment.GetEnvironmentVariable("FRAMEWRIGHT_VOICE_WORKER_TOKEN") ?? String.Empty;
        if (port <= 0 || token.Length < 32) return 64;

        TcpListener listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        while (true)
        {
            using (TcpClient client = listener.AcceptTcpClient())
            using (NetworkStream stream = client.GetStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
            {
                bool authorized = false;
                string line;
                while (!String.IsNullOrEmpty(line = reader.ReadLine()))
                {
                    const string Header = "X-Framewright-Voice-Token:";
                    if (line.StartsWith(Header, StringComparison.OrdinalIgnoreCase) &&
                        String.Equals(line.Substring(Header.Length).Trim(), token, StringComparison.Ordinal))
                    {
                        authorized = true;
                    }
                }
                string body = authorized ? "{\"status\":\"ready\"}" : "{\"error\":\"unauthorized\"}";
                byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                string status = authorized ? "200 OK" : "401 Unauthorized";
                byte[] headers = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 " + status + "\r\nContent-Type: application/json\r\nContent-Length: " +
                    bodyBytes.Length + "\r\nConnection: close\r\n\r\n");
                stream.Write(headers, 0, headers.Length);
                stream.Write(bodyBytes, 0, bodyBytes.Length);
                stream.Flush();
            }
        }
    }
}
'@
    $sourcePath = Join-Path $testRoot 'fake-python.cs'
    [IO.File]::WriteAllText($sourcePath, $source, [Text.UTF8Encoding]::new($false))
    $compiler = @(
        (Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $compiler) { throw 'The Windows .NET Framework C# compiler is required for the voice PID contract test.' }
    & $compiler /nologo /target:exe "/out:$fakePython" $sourcePath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $fakePython -PathType Leaf)) {
        throw "Could not compile the disposable authenticated voice worker (exit $LASTEXITCODE)."
    }

    $scripts = Join-Path $testRoot 'scripts'
    $voiceTools = Join-Path $testRoot 'tools\voice'
    $runtimeRoot = Join-Path $testRoot 'runtime'
    New-Item -ItemType Directory -Path $scripts, $voiceTools, (Join-Path $runtimeRoot 'packages\qwen_tts') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\start-voice-worker.ps1') -Destination $scripts
    $workerScript = Join-Path $voiceTools 'qwen_voice_worker.py'
    $modelLock = Join-Path $voiceTools 'models.lock.json'
    $manifest = Join-Path $runtimeRoot 'runtime-manifest.json'
    [IO.File]::WriteAllText($workerScript, '# fake worker contract fixture', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($modelLock, '{}', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($manifest, '{}', [Text.UTF8Encoding]::new($false))
    $token = '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'
    $stateRoot = Join-Path $runtimeRoot 'state'
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $stateRoot 'voice-worker.token'), $token, [Text.UTF8Encoding]::new($false))

    $portProbe = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $portProbe.Start()
    $workerPort = ([Net.IPEndPoint]$portProbe.LocalEndpoint).Port
    $portProbe.Stop()

    # Deliberately claim that this PowerShell process is the worker. A reused PID
    # must fail the executable/command-line proof and must never be killed, even
    # when the caller explicitly requests a restart.
    $unrelated = Get-Process -Id $PID
    $staleState = [ordered]@{
        schemaVersion = 1
        pid = $unrelated.Id
        processStartTimeUtc = ([DateTimeOffset]$unrelated.StartTime.ToUniversalTime()).ToString('O')
        python = [IO.Path]::GetFullPath($fakePython)
        workerScript = [IO.Path]::GetFullPath($workerScript)
        manifest = [IO.Path]::GetFullPath($manifest)
        modelLock = [IO.Path]::GetFullPath($modelLock)
        port = $workerPort
    } | ConvertTo-Json
    $pidPath = Join-Path $stateRoot 'voice-worker.pid'
    [IO.File]::WriteAllText($pidPath, $staleState, [Text.UTF8Encoding]::new($false))

    $launcher = Join-Path $scripts 'start-voice-worker.ps1'
    & $launcher -Restart -VoiceRuntimeRoot $runtimeRoot -VoicePythonPath $fakePython `
        -WorkerPort $workerPort -StartupTimeoutSeconds 10 -SkipUserEnvironmentUpdate | Out-Null
    if (-not (Get-Process -Id $unrelated.Id -ErrorAction SilentlyContinue)) {
        throw 'Restart killed a process whose PID was present but whose identity did not belong to Framewright.'
    }

    $firstState = Get-Content -LiteralPath $pidPath -Raw | ConvertFrom-Json
    $firstWorkerId = [int]$firstState.pid
    $ownedWorkerIds.Add($firstWorkerId)
    if ($firstWorkerId -eq $unrelated.Id -or -not (Get-Process -Id $firstWorkerId -ErrorAction SilentlyContinue)) {
        throw 'A fresh owned worker was not launched after stale PID bookkeeping was discarded.'
    }
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$workerPort/health" `
        -Headers @{ 'X-Framewright-Voice-Token' = $token } -TimeoutSec 2
    if ($health.status -ne 'ready') { throw 'The replacement worker did not pass authenticated health.' }

    Write-Host 'Voice PID ownership test passed: a reused PID is never killed, and a fresh worker launches with authenticated health.'
}
finally {
    foreach ($workerId in $ownedWorkerIds) {
        $worker = Get-Process -Id $workerId -ErrorAction SilentlyContinue
        if ($worker -and $worker.Path -and
            [IO.Path]::GetFullPath($worker.Path).StartsWith([IO.Path]::GetFullPath($testRoot), [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $workerId -Force -ErrorAction SilentlyContinue
            $null = $worker.WaitForExit(5000)
        }
    }
    $resolvedTest = [IO.Path]::GetFullPath($testRoot)
    if ($resolvedTest.StartsWith($testBase, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTest).StartsWith('framewright-voice-pid-test-', [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTest)) {
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force
    }
}

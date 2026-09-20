<#
.SYNOPSIS
  A controlled stand-in for the Reference Asset Compiler, for browser journeys.

.DESCRIPTION
  M07 is proved with controlled worker output rather than GPU work: the point is
  the application's own behaviour, not the compiler's. This answers the two
  commands the studio actually sends, in exactly the shape the real `rac`
  answers them, so the journey exercises the real gateway, the real process
  boundary, the real job queue and the real model import -- with a known GLB
  instead of an hour of hardware.

  It is deliberately not clever. A stub that drifts from the real contract is
  worse than no stub, so it speaks the same JSON and writes the same receipt.
#>
param(
    # Everything the studio passed, in order. A stand-in has to accept the same
    # shape of command line the real compiler does.
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Arguments = @()
)

$arguments = @($Arguments)
$repoRoot = Split-Path -Parent $PSScriptRoot

function Read-Option {
    param([string] $Name)
    for ($index = 0; $index -lt $arguments.Count; $index++) {
        if ($arguments[$index] -eq $Name -and ($index + 1) -lt $arguments.Count) {
            return $arguments[$index + 1]
        }
    }
    return $null
}

if ($arguments -contains '--version') {
    Write-Output 'reference-asset-compiler 0.1.2 (e2e stand-in)'
    exit 0
}

if ($arguments.Count -ge 1 -and $arguments[0] -eq 'run-stage') {
    if ($arguments -contains '--list') {
        $report = [ordered]@{
            ok       = $true
            checkout = $repoRoot
            blender  = 'e2e-stand-in'
            stages   = @(
                [ordered]@{
                    stage     = 'browser-payload'
                    runner    = 'blender'
                    summary   = 'Export the staged asset as a self-contained browser GLB, +Y up and metric.'
                    produces  = 'reference-asset-compiler.browser-payload.v1'
                    arguments = @('source', 'output', 'report')
                    available = $true
                    missing   = @()
                }
            )
        }
        Write-Output ($report | ConvertTo-Json -Depth 6)
        exit 0
    }

    $source = Read-Option '--source'
    $output = Read-Option '--output'
    $receipt = Read-Option '--report'
    if (-not $source -or -not $output -or -not $receipt) {
        Write-Error 'RAC_ERROR run-stage needs --source, --output and --report'
        exit 2
    }

    # A real rigged model, so the studio's import validates a real GLB and the
    # delivered asset is genuinely inspectable in the browser.
    $fixture = Join-Path $repoRoot 'fixtures/glb/rigged-figure.glb'
    if (-not (Test-Path $fixture)) {
        Write-Error "RAC_ERROR the stand-in fixture is missing: $fixture"
        exit 2
    }

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
    Copy-Item -LiteralPath $fixture -Destination $output -Force
    $payloadHash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()

    $stageReceipt = [ordered]@{
        schema          = 'reference-asset-compiler.browser-payload.v1'
        blender_version = 'e2e stand-in'
        source          = $source
        source_sha256   = $sourceHash
        payload         = $output
        payload_sha256  = $payloadHash
        payload_bytes   = (Get-Item -LiteralPath $output).Length
        convention      = [ordered]@{ up = '+Y'; units = 'metres'; self_contained = $true }
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $receipt) | Out-Null
    Set-Content -LiteralPath $receipt -Value ($stageReceipt | ConvertTo-Json -Depth 6) -Encoding utf8

    $payload = [ordered]@{
        ok        = $true
        schema    = 'reference-asset-compiler.stage-run.v1'
        stage     = 'browser-payload'
        runner    = 'blender'
        blender   = 'e2e-stand-in'
        exit_code = 0
        seconds   = 0.2
        report    = $receipt
        receipt   = $stageReceipt
    }
    Write-Output ($payload | ConvertTo-Json -Depth 8)
    exit 0
}

Write-Error "RAC_ERROR unknown command: $($arguments -join ' ')"
exit 2

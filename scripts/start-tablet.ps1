[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Framewright'),
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [switch]$AcknowledgeTrustedPrivateNetwork
)

$ErrorActionPreference = 'Stop'
$resolvedInstall = [IO.Path]::GetFullPath($InstallRoot)
$executable = Join-Path $resolvedInstall 'Framewright.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Installed application not found at $executable"
}

$addresses = Get-NetIPAddress -AddressFamily IPv4 -AddressState Preferred -ErrorAction SilentlyContinue |
    Where-Object { -not $_.IPAddress.StartsWith('127.') -and $_.PrefixOrigin -ne 'WellKnown' } |
    Select-Object -ExpandProperty IPAddress -Unique

$scheme = 'https'
if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    $resolvedCertificate = [IO.Path]::GetFullPath($CertificatePath)
    if (-not (Test-Path -LiteralPath $resolvedCertificate -PathType Leaf) -or [IO.Path]::GetExtension($resolvedCertificate) -ne '.pfx') {
        throw "CertificatePath must identify a PFX certificate: $resolvedCertificate"
    }
    if ([string]::IsNullOrEmpty($CertificatePassword)) {
        $secure = Read-Host 'PFX password' -AsSecureString
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { $CertificatePassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    }
    $env:ASPNETCORE_Kestrel__Certificates__Default__Path = $resolvedCertificate
    $env:ASPNETCORE_Kestrel__Certificates__Default__Password = $CertificatePassword
}
elseif ($AcknowledgeTrustedPrivateNetwork) {
    $scheme = 'http'
    Write-Warning 'HTTP tablet mode protects application access with pairing but does not encrypt LAN traffic. Use only on a trusted private network.'
}
else {
    throw 'Tablet mode requires a trusted PFX certificate. Supply -CertificatePath, or explicitly acknowledge unencrypted HTTP with -AcknowledgeTrustedPrivateNetwork.'
}

$env:Urls = "$scheme`://0.0.0.0:5179"
$env:Studio__AllowLan = 'true'
Write-Host "Tablet mode is enabled over $($scheme.ToUpperInvariant()) for this process only. Pair the tablet using the code shown in Setup."
foreach ($address in $addresses) { Write-Host ("  {0}://{1}:5179" -f $scheme, $address) }
Write-Host 'The application does not create firewall rules. Press Ctrl+C to stop tablet access.'
try { & $executable }
finally {
    Remove-Item Env:ASPNETCORE_Kestrel__Certificates__Default__Password -ErrorAction SilentlyContinue
    $CertificatePassword = $null
}

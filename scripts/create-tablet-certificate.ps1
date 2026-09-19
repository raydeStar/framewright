[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $env:LOCALAPPDATA 'Framewright\certificates'),
    [string]$DnsName = $env:COMPUTERNAME
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') { throw 'Tablet certificate creation is available only on Windows.' }
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$localAppData = [IO.Path]::GetFullPath($env:LOCALAPPDATA)
$localAppDataPrefix = $localAppData.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($resolvedOutput -eq $localAppData -or -not $resolvedOutput.StartsWith($localAppDataPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be a dedicated folder inside LocalAppData: $resolvedOutput"
}
if ($DnsName -notmatch '^[A-Za-z0-9.-]+$') { throw "DnsName contains unsupported characters: $DnsName" }
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null

$addresses = Get-NetIPAddress -AddressFamily IPv4 -AddressState Preferred -ErrorAction SilentlyContinue |
    Where-Object { -not $_.IPAddress.StartsWith('127.') -and $_.PrefixOrigin -ne 'WellKnown' } |
    Select-Object -ExpandProperty IPAddress -Unique
$sanParts = @("DNS=$DnsName", 'DNS=localhost') + @($addresses | ForEach-Object { "IPAddress=$_" })
$sanExtension = '2.5.29.17={text}' + ($sanParts -join '&')
$password = Read-Host 'Choose a password for the PFX' -AsSecureString
$certificate = New-SelfSignedCertificate -Subject "CN=$DnsName" -Type SSLServerAuthentication -TextExtension @($sanExtension) -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(2) -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable
$pfxPath = Join-Path $resolvedOutput 'framewright-tablet.pfx'
$cerPath = Join-Path $resolvedOutput 'framewright-tablet.cer'
Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $password | Out-Null
Export-Certificate -Cert $certificate -FilePath $cerPath -Type CERT | Out-Null

Write-Host "PFX created: $pfxPath"
Write-Host "Public certificate created: $cerPath"
Write-Host 'Install the CER as a trusted certificate on each tablet before connecting. Keep the PFX and its password on the workstation only.'

[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$OtaPrivateKeyPath = "$env:LOCALAPPDATA\WinputLan\release\ota-private.pem"
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required to sign the OTA manifest.' }
if (-not (Test-Path -LiteralPath $OtaPrivateKeyPath)) { throw 'OTA private key is required. Create it with scripts/new-ota-signing-key.ps1 or provide OTA_SIGNING_PRIVATE_KEY_PEM in CI.' }
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build-installer.ps1') -Configuration $Configuration
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$out = Join-Path $root "artifacts/WinputLan-$version"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$setupName = "WinputLan-$version-setup.exe"
$setup = Join-Path $root "artifacts/installer/$setupName"
Copy-Item $setup $out -Force
$setupHash = (Get-FileHash (Join-Path $out $setupName) -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    Version = $version
    AssetName = $setupName
    AssetUrl = "https://github.com/luingry/WinputLan/releases/download/v$version/$setupName"
    Sha256 = $setupHash
    NotesUrl = "https://github.com/luingry/WinputLan/releases/tag/v$version"
    Algorithm = 'RSA-PKCS1-SHA256'
    KeyId = 'winputlan-ota-rsa-2026-09'
}
$payload = @($manifest.Version, $manifest.AssetName, $manifest.AssetUrl, $manifest.Sha256, $manifest.NotesUrl) -join "`n"
$rsa = [System.Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([char[]](Get-Content -LiteralPath $OtaPrivateKeyPath -Raw))
    $manifest.Signature = [Convert]::ToBase64String($rsa.SignData([Text.Encoding]::UTF8.GetBytes($payload), [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1))
}
finally { $rsa.Dispose() }
$hashes = Get-ChildItem $out -File | Sort-Object Name | ForEach-Object { $h = Get-FileHash $_.FullName -Algorithm SHA256; [pscustomobject]@{ name=$_.Name; sha256=$h.Hash.ToLowerInvariant(); size=$_.Length } }
$hashes | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $out 'SHA256.json') -Encoding UTF8
@("Winput LAN $version", "Generated $(Get-Date -Format o)", "Files: $($hashes.Count)") | Set-Content (Join-Path $out 'MANIFEST.txt') -Encoding UTF8
$manifest | ConvertTo-Json | Set-Content (Join-Path $out 'update-manifest.json') -Encoding UTF8
Write-Host "Finalized OTA-verified package: $out" -ForegroundColor Green

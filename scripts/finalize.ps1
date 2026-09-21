[CmdletBinding()]
param([string]$Configuration = 'Release', [string]$CertificatePath, [string]$CertificatePassword)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build-installer.ps1') -Configuration $Configuration -CertificatePath $CertificatePath -CertificatePassword $CertificatePassword
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$out = Join-Path $root "artifacts/WinputLan-$version"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$setupName = "WinputLan-$version-setup.exe"
$setup = Join-Path $root "artifacts/installer/$setupName"
Copy-Item $setup $out -Force
$hashes = Get-ChildItem $out -File | Sort-Object Name | ForEach-Object { $h = Get-FileHash $_.FullName -Algorithm SHA256; [pscustomobject]@{ name=$_.Name; sha256=$h.Hash.ToLowerInvariant(); size=$_.Length } }
$hashes | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $out 'SHA256.json') -Encoding UTF8
@("Winput LAN $version", "Generated $(Get-Date -Format o)", "Files: $($hashes.Count)") | Set-Content (Join-Path $out 'MANIFEST.txt') -Encoding UTF8
$setupHash = (Get-FileHash (Join-Path $out $setupName) -Algorithm SHA256).Hash.ToLowerInvariant()
([pscustomobject]@{
    Version = $version
    AssetName = $setupName
    AssetUrl = "https://github.com/luingry/WinputLan/releases/download/v$version/$setupName"
    Sha256 = $setupHash
    AuthenticodeRequired = $true
    NotesUrl = "https://github.com/luingry/WinputLan/releases/tag/v$version"
} | ConvertTo-Json) | Set-Content (Join-Path $out 'update-manifest.json') -Encoding UTF8
Write-Host "Finalized package: $out" -ForegroundColor Green

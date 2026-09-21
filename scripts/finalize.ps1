[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$out = Join-Path $root "artifacts/WinputLan-$version"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$appOut = Join-Path $root "src/WinputLan/bin/$Configuration/net48"
Copy-Item (Join-Path $appOut 'WinputLan.exe') $out -Force
Copy-Item (Join-Path $appOut 'WinputLan.Core.dll') $out -Force
Copy-Item (Join-Path $root 'assets/brand/winput-lan.ico') $out -Force
$packageBrand = Join-Path $out 'assets/brand'
New-Item -ItemType Directory -Force -Path $packageBrand | Out-Null
Copy-Item (Join-Path $root 'assets/brand/icon-normalized.png') $packageBrand -Force
Copy-Item (Join-Path $root 'README.md') $out -Force
Copy-Item (Join-Path $root 'CHANGELOG.md') $out -Force
$hashes = Get-ChildItem $out -File | Sort-Object Name | ForEach-Object { $h = Get-FileHash $_.FullName -Algorithm SHA256; [pscustomobject]@{ name=$_.Name; sha256=$h.Hash.ToLowerInvariant(); size=$_.Length } }
$hashes | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $out 'SHA256.json') -Encoding UTF8
@("Winput LAN $version", "Generated $(Get-Date -Format o)", "Files: $($hashes.Count)") | Set-Content (Join-Path $out 'MANIFEST.txt') -Encoding UTF8
$exeHash = (Get-FileHash (Join-Path $out 'WinputLan.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
([pscustomobject]@{
    Version = $version
    AssetName = 'WinputLan.exe'
    AssetUrl = "https://github.com/luingry/WinputLan/releases/download/v$version/WinputLan.exe"
    Sha256 = $exeHash
    AuthenticodeRequired = $true
    NotesUrl = "https://github.com/luingry/WinputLan/releases/tag/v$version"
} | ConvertTo-Json) | Set-Content (Join-Path $out 'update-manifest.json') -Encoding UTF8
Write-Host "Finalized package: $out" -ForegroundColor Green

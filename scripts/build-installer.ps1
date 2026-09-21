[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if (-not $iscc) { throw 'Inno Setup ISCC.exe is not installed. Install it explicitly, then rerun this script.' }
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
& $iscc.Source "/DAppVersion=$version" (Join-Path $root 'installer/WinputLan.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }
Write-Host 'Inno Setup package created under artifacts/installer.' -ForegroundColor Green

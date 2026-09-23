[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if (-not $iscc) { $iscc = @("$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1 }
if (-not $iscc) { throw 'Inno Setup ISCC.exe is not installed. Install it explicitly, then rerun this script.' }
$isccPath = if ($iscc -is [string]) { $iscc } elseif ($iscc.PSObject.Properties['Source']) { $iscc.Source } else { $iscc.FullName }
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
& $isccPath "/DAppVersion=$version" (Join-Path $root 'installer/WinputLan.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }
$setup = Join-Path $root "artifacts/installer/WinputLan-$version-setup.exe"
if (-not (Test-Path $setup)) { throw 'Inno Setup did not create the expected setup executable.' }
Write-Host 'Unsigned Inno Setup package created under artifacts/installer.' -ForegroundColor Green

[CmdletBinding()]
param([string]$Configuration = 'Release', [string]$CertificatePath, [string]$CertificatePassword)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if (-not $iscc) { throw 'Inno Setup ISCC.exe is not installed. Install it explicitly, then rerun this script.' }
if ([string]::IsNullOrWhiteSpace($CertificatePath) -or -not (Test-Path $CertificatePath)) { throw 'A code-signing certificate path is required; packaging fails closed without it.' }
$signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if (-not $signTool) { throw 'signtool.exe is required; packaging fails closed without Authenticode verification.' }
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
$exe = Join-Path $root "src/WinputLan/bin/$Configuration/net48/WinputLan.exe"
& $signTool.Source sign /fd SHA256 /f $CertificatePath /p $CertificatePassword $exe
if ($LASTEXITCODE -ne 0) { throw "Authenticode signing the application failed with exit code $LASTEXITCODE." }
& $signTool.Source verify /pa /all $exe
if ($LASTEXITCODE -ne 0) { throw 'Authenticode verification of the application failed.' }
& $iscc.Source "/DAppVersion=$version" (Join-Path $root 'installer/WinputLan.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }
$setup = Join-Path $root "artifacts/installer/WinputLan-$version-setup.exe"
if (-not (Test-Path $setup)) { throw 'Inno Setup did not create the expected setup executable.' }
& $signTool.Source sign /fd SHA256 /f $CertificatePath /p $CertificatePassword $setup
if ($LASTEXITCODE -ne 0) { throw "Authenticode signing the setup failed with exit code $LASTEXITCODE." }
& $signTool.Source verify /pa /all $setup
if ($LASTEXITCODE -ne 0) { throw 'Authenticode verification of the setup failed.' }
Write-Host 'Inno Setup package created under artifacts/installer.' -ForegroundColor Green

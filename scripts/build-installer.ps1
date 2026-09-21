[CmdletBinding()]
param([string]$Configuration = 'Release', [string]$CertificatePath, [string]$CertificatePassword)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if (-not $iscc) { $iscc = @("$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1 }
if (-not $iscc) { throw 'Inno Setup ISCC.exe is not installed. Install it explicitly, then rerun this script.' }
if ([string]::IsNullOrWhiteSpace($CertificatePath) -or -not (Test-Path $CertificatePath)) { throw 'A code-signing certificate path is required; packaging fails closed without it.' }
$signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if (-not $signTool -and (Test-Path "$env:ProgramFiles(x86)\Windows Kits\10\bin")) { $signTool = Get-ChildItem "$env:ProgramFiles(x86)\Windows Kits\10\bin" -Filter signtool.exe -Recurse | Sort-Object FullName -Descending | Select-Object -First 1 }
if (-not $signTool) { throw 'signtool.exe is required; packaging fails closed without Authenticode verification.' }
$isccPath = if ($iscc -is [string]) { $iscc } elseif ($iscc.PSObject.Properties['Source']) { $iscc.Source } else { $iscc.FullName }
$signToolPath = if ($signTool -is [string]) { $signTool } elseif ($signTool.PSObject.Properties['Source']) { $signTool.Source } else { $signTool.FullName }
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
$exe = Join-Path $root "src/WinputLan/bin/$Configuration/net48/WinputLan.exe"
& $signToolPath sign /fd SHA256 /f $CertificatePath /p $CertificatePassword $exe
if ($LASTEXITCODE -ne 0) { throw "Authenticode signing the application failed with exit code $LASTEXITCODE." }
& $signToolPath verify /pa /all $exe
if ($LASTEXITCODE -ne 0) { throw 'Authenticode verification of the application failed.' }
& $isccPath "/DAppVersion=$version" (Join-Path $root 'installer/WinputLan.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }
$setup = Join-Path $root "artifacts/installer/WinputLan-$version-setup.exe"
if (-not (Test-Path $setup)) { throw 'Inno Setup did not create the expected setup executable.' }
& $signToolPath sign /fd SHA256 /f $CertificatePath /p $CertificatePassword $setup
if ($LASTEXITCODE -ne 0) { throw "Authenticode signing the setup failed with exit code $LASTEXITCODE." }
& $signToolPath verify /pa /all $setup
if ($LASTEXITCODE -ne 0) { throw 'Authenticode verification of the setup failed.' }
Write-Host 'Inno Setup package created under artifacts/installer.' -ForegroundColor Green

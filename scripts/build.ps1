[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw "VERSION is not SemVer: $version" }
$solution = Join-Path $root 'WinputLan.sln'
dotnet restore $solution
dotnet build $solution -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
dotnet run --project (Join-Path $root 'tests/WinputLan.Tests/WinputLan.Tests.csproj') -c $Configuration --no-build
if ($LASTEXITCODE -ne 0) { throw 'Core test suite failed.' }
$loopback = Join-Path $root "tests/WinputLan.Loopback/bin/$Configuration/net48/WinputLan.Loopback.exe"
& $loopback
if ($LASTEXITCODE -ne 0) { throw 'Loopback smoke failed.' }
Write-Host "Winput LAN $version $Configuration build and tests passed." -ForegroundColor Green

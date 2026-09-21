[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
if ((Get-Content (Join-Path $root 'Directory.Build.props') -Raw) -notmatch 'ReadAllText.*VERSION') { throw 'Directory.Build.props must be the single VERSION source.' }
if (Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.csproj | Select-String -SimpleMatch '<Version>') { throw 'Project files must not duplicate VERSION.' }
if ((Get-Content (Join-Path $root 'CHANGELOG.md') -Raw) -notmatch "## \[$([regex]::Escape($version))\]") { throw "CHANGELOG has no heading for $version" }
foreach ($path in @('docs/PLAN.md','docs/ARCHITECTURE.md','docs/SECURITY.md','docs/RELEASING.md','docs/PROTOCOL.md','docs/TESTING.md','docs/DESIGN.md','assets/brand/prototype-master.png','assets/brand/icon-master.png','assets/brand/icon-normalized.png','assets/brand/winput-lan.ico','installer/WinputLan.iss')) {
    if (-not (Test-Path (Join-Path $root $path))) { throw "Missing release invariant: $path" }
}
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
$exe = Join-Path $root "src/WinputLan/bin/$Configuration/net48/WinputLan.exe"
if (-not (Test-Path $exe)) { throw 'Release executable is missing.' }
$size = (Get-Item $exe).Length
if ($size -lt 10000) { throw "Executable is unexpectedly small: $size bytes" }
Write-Host "Release invariants passed for $version ($size bytes)." -ForegroundColor Green

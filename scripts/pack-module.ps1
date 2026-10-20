<#
    Builds the SwissTechKnife module package (catalog id winput-lan):
      artifacts/winput-lan-<VERSION>.zip
        module.json, WinputLan.Module.dll            (loaded by the host, from memory)
        app/WinputLan.Hosted.exe (+ dlls, runtimes/)  (the Winput LAN app, run in its own process with --hosted)
    The SDK dll and WPF/.NET Framework assemblies are never packaged: the host and Windows provide them.
    The module version is VERSION. Prints sha256 and size for the host catalog.
    powershell -File scripts/pack-module.ps1
#>
[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$moduleProject = Join-Path $root 'src/WinputLan.Module'
$appProject = Join-Path $root 'src/WinputLan'
$manifest = Get-Content (Join-Path $moduleProject 'module.json') -Raw | ConvertFrom-Json
if ($manifest.version -ne $version) { throw "src/WinputLan.Module/module.json version $($manifest.version) does not match VERSION $version." }

foreach ($project in $moduleProject, $appProject) {
    dotnet build $project -c $Configuration -v q -nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}

$moduleOut = Join-Path $moduleProject "bin/$Configuration/net48"
$appOut = Join-Path $appProject "bin/$Configuration/net48"
if (-not (Test-Path (Join-Path $appOut 'WinputLan.exe'))) { throw "WinputLan.exe not found in $appOut" }

$outDir = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force $outDir | Out-Null
$zipPath = Join-Path $outDir "$($manifest.id)-$version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }

$skip = '.pdb', '.xml'
# Shared or framework assemblies the host process / Windows already provide.
$forbidden = '^(SwissTechKnife\.Sdk|PresentationCore|PresentationFramework.*|WindowsBase|System(\..*)?|mscorlib|netstandard|Microsoft\.CSharp)\.dll$'
$zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    function Add-Entry([string]$file, [string]$entry) {
        if ((Split-Path $entry -Leaf) -match $forbidden) { throw "Refusing to package $entry" }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file, $entry, 'Optimal') | Out-Null
    }
    Get-ChildItem $moduleOut -File |
        Where-Object { $_.Name -notlike 'SwissTechKnife.Sdk.*' -and $_.Extension -notin $skip } |
        ForEach-Object { Add-Entry $_.FullName $_.Name }
    $appRoot = (Resolve-Path $appOut).Path.TrimEnd('\') + '\'
    Get-ChildItem $appOut -File -Recurse |
        Where-Object { $_.Extension -notin $skip } |
        ForEach-Object {
            $relative = $_.FullName.Substring($appRoot.Length).Replace('\', '/')
            # Own image name: never mistaken for (or closed with) the standalone WinputLan.exe.
            if ($relative -eq 'WinputLan.exe') { $relative = 'WinputLan.Hosted.exe' }
            if ($relative -eq 'WinputLan.exe.config') { $relative = 'WinputLan.Hosted.exe.config' }
            Add-Entry $_.FullName "app/$relative"
        }
} finally { $zip.Dispose() }

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $zipPath).Length
Write-Host "Packed $zipPath" -ForegroundColor Green
Write-Host "  version $version"
Write-Host "  sha256  $hash"
Write-Host "  size    $size"
[pscustomobject]@{ Id = $manifest.id; Version = $version; File = $zipPath; Sha256 = $hash; Size = $size }

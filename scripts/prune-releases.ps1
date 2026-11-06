[CmdletBinding()]
param(
    [string]$CatalogUrl = 'https://raw.githubusercontent.com/luingry/SwissTechKnife/main/catalog/catalog.json',
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'

# One snapshot: a release published after this call is not in it, so it can never be deleted here.
$json = (gh release list --limit 1000 --json 'tagName,isLatest') -join "`n"
if ($LASTEXITCODE -ne 0) { throw 'Could not list releases.' }
$releases = @($json | ConvertFrom-Json) | ForEach-Object { $_ }
$latest = @($releases | Where-Object { $_.isLatest })
if ($latest.Count -ne 1) { throw "Expected one latest release, found $($latest.Count)." }
$tag = $latest[0].tagName
$older = @($releases | Where-Object { $_.tagName -ne $tag } | ForEach-Object { $_.tagName })
if ($older.Count -eq 0) {
    Write-Output "Only $tag exists; nothing to delete."
    exit 0
}

# Installs download the package URL in the catalog, so the release it names must outlive the catalog update.
$catalog = (Invoke-WebRequest -Uri $CatalogUrl -UseBasicParsing).Content | ConvertFrom-Json
$entry = @($catalog.modules | Where-Object { $_.id -eq 'winput-lan' })
if ($entry.Count -ne 1) { throw 'winput-lan is not in the catalog.' }
$expected = "https://github.com/luingry/WinputLan/releases/download/$tag/winput-lan-$($tag.TrimStart('v')).zip"
if ($entry[0].package -ne $expected) {
    Write-Output "Catalog still points to $($entry[0].package); keeping: $($older -join ', ')."
    exit 0
}

foreach ($old in $older) {
    if ($DryRun) {
        Write-Output "Would delete $old."
        continue
    }
    gh release delete $old --yes
    if ($LASTEXITCODE -ne 0) { throw "Failed to delete release $old" }
    Write-Output "Deleted $old."
}

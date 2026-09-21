[CmdletBinding()]
param([string]$Version = (Get-Content (Join-Path (Split-Path -Parent $PSScriptRoot) 'VERSION') -Raw).Trim())
$ErrorActionPreference = 'Stop'
$text = Get-Content (Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md') -Raw
$escaped = [regex]::Escape($Version)
$match = [regex]::Match($text, "(?ms)^## \[$escaped\].*?(?=^## \[|\z)")
if (-not $match.Success) { throw "No changelog entry for $Version" }
$match.Value.Trim()

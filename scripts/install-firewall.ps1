[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory=$true)][string]$ProgramPath,
    [int]$Port = 45900
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ProgramPath)) { throw "Program not found: $ProgramPath" }
if ($Port -lt 1 -or $Port -gt 65535) { throw 'Port must be 1..65535.' }
$name = 'Winput LAN (Private TCP)'
$description = 'Winput LAN secure peer input transport; Private profile only.'
$arguments = "advfirewall firewall add rule name=\"$name\" description=\"$description\" dir=in action=allow enable=yes profile=Private protocol=TCP localport=$Port program=\"$ProgramPath\""
if ($PSCmdlet.ShouldProcess("Private TCP $Port for $ProgramPath", 'Create explicit inbound firewall rule')) {
    $process = Start-Process netsh.exe -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "netsh failed with exit code $($process.ExitCode)." }
    Write-Host "Created '$name' for Private profile only." -ForegroundColor Green
}
